using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SpireReplay.Media;

/// <summary>Captures the audio rendered by the Windows game process into a 16-bit stereo PCM WAV.</summary>
public sealed class GameAudioCapture : IDisposable
{
    public int SampleRate => 48000;
    public int Channels => 2;
    public long FramesWritten => Interlocked.Read(ref written);
    public long StartedTimestamp { get; private set; }

    private readonly string path;
    private readonly ManualResetEvent stop = new(false);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread worker;
    private readonly object lifetime = new();
    private long written;
    private Exception? failure;
    private bool disposed;

    [SupportedOSPlatform("windows10.0.20348")]
    private GameAudioCapture(string path)
    {
        this.path = path;
        worker = new Thread(Record) { IsBackground = true, Name = "Replay the Spire audio" };
    }

    public static GameAudioCapture Start(string wavPath)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("游戏音轨导出需要 Windows 10 build 20348 或更高版本的 64 位系统。");
        var capture = new GameAudioCapture(Path.GetFullPath(wavPath));
        capture.worker.SetApartmentState(ApartmentState.MTA);
        capture.worker.Start();
        try
        {
            capture.ready.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            return capture;
        }
        catch
        {
            capture.stop.Set();
            capture.worker.Join();
            capture.stop.Dispose();
            throw;
        }
    }

    public void Stop()
    {
        lock (lifetime)
        {
            if (disposed) return;
            disposed = true;
            stop.Set();
            worker.Join();
            stop.Dispose();
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public void Dispose() => Stop();

    [SupportedOSPlatform("windows10.0.20348")]
    private void Record()
    {
        AudioInterfaces.IAudioClient? client = null;
        AudioInterfaces.ICaptureClient? packets = null;
        bool started = false;
        try
        {
            using var signal = new AutoResetEvent(false);
            using var wave = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
            wave.Write(new byte[44]);
            client = AudioInterfaces.Activate();
            var format = new AudioInterfaces.WaveFormat {
                Tag = 1, Channels = 2, Rate = (uint)SampleRate, BytesPerSecond = (uint)(SampleRate * 4), Align = 4, Bits = 16
            };
            const uint streamFlags = 0x20000u | 0x40000u | 0x80000000u; // loopback, event callback, PCM conversion
            Marshal.ThrowExceptionForHR(client.Initialize(0, streamFlags, 0, 0, ref format, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(client.GetBufferSize(out var maximum));
            if (maximum is 0 or > 524288) throw new InvalidDataException("Unexpected Windows capture buffer size.");
            var scratch = new byte[maximum * 4];
            var silence = new byte[16384];
            var iid = typeof(AudioInterfaces.ICaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(in iid, out var service));
            packets = (AudioInterfaces.ICaptureClient)service;
            Marshal.ThrowExceptionForHR(client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle()));
            var waits = new WaitHandle[] { stop, signal };
            StartedTimestamp = Stopwatch.GetTimestamp();
            Marshal.ThrowExceptionForHR(client.Start());
            started = true;
            ready.TrySetResult();
            while (WaitHandle.WaitAny(waits) != 0)
                Drain(packets, wave, scratch, silence, stopping: false);
            var end = Stopwatch.GetTimestamp();
            Marshal.ThrowExceptionForHR(client.Stop());
            started = false;
            Drain(packets, wave, scratch, silence, stopping: true);
            var endingFrame = (long)Math.Round(Stopwatch.GetElapsedTime(StartedTimestamp, end).TotalSeconds * SampleRate);
            // A silent process may supply no packets. Keep silence on the same real-time clock as the video.
            if (written < endingFrame) Pad(wave, endingFrame - written, silence);
            else if (written > endingFrame)
            {
                wave.SetLength(44 + endingFrame * 4);
                Interlocked.Exchange(ref written, endingFrame);
            }
            WriteHeader(wave);
        }
        catch (Exception error) { failure = error; ready.TrySetException(error); }
        finally
        {
            if (started) client?.Stop();
            if (packets != null) Marshal.ReleaseComObject(packets);
            if (client != null) Marshal.ReleaseComObject(client);
        }
    }

    [SupportedOSPlatform("windows10.0.20348")]
    private void Drain(AudioInterfaces.ICaptureClient packets, FileStream wave, byte[] scratch, byte[] silence, bool stopping)
    {
        ulong epoch = (ulong)(StartedTimestamp / (double)Stopwatch.Frequency * 10_000_000);
        while (stopping || !stop.WaitOne(0))
        {
            Marshal.ThrowExceptionForHR(packets.GetNextPacketSize(out var size));
            if (size == 0) break;
            Marshal.ThrowExceptionForHR(packets.GetBuffer(out var data, out var frames, out var flags, out _, out var qpc));
            int bytes = checked((int)frames * 4);
            try
            {
                if (bytes > scratch.Length) throw new InvalidDataException("Audio packet exceeds negotiated size.");
                if ((flags & 2) != 0) Array.Clear(scratch, 0, bytes);
                else Marshal.Copy(data, scratch, 0, bytes);
            }
            finally { Marshal.ThrowExceptionForHR(packets.ReleaseBuffer(frames)); }

            // QPC timestamps use 100 ns units. Preserve gaps without shifting later SFX earlier.
            int skipFrames = 0;
            if ((flags & 4) == 0 && qpc != 0)
            {
                long packetStart = (long)Math.Round(((long)qpc - (long)epoch) * (double)SampleRate / 10_000_000);
                if (packetStart > written + SampleRate / 200)
                    Pad(wave, packetStart - written, silence);
                else if (packetStart < written - SampleRate / 200 || (written == 0 && packetStart < 0))
                    skipFrames = (int)Math.Min(frames, written - packetStart);
            }
            var remaining = frames - skipFrames;
            CheckSize(written + remaining);
            wave.Write(scratch, skipFrames * 4, bytes - skipFrames * 4);
            Interlocked.Add(ref written, remaining);
        }
    }

    private void Pad(FileStream wave, long count, byte[] silence)
    {
        CheckSize(written + count);
        while (count > 0)
        {
            int frames = (int)Math.Min(count, silence.Length / 4);
            wave.Write(silence, 0, frames * 4);
            Interlocked.Add(ref written, frames);
            count -= frames;
        }
    }

    private static void CheckSize(long frames)
    {
        if (frames < 0 || frames * 4 > uint.MaxValue - 36)
            throw new IOException("本段 PCM 音轨超过 WAV 容量。");
    }

    private void WriteHeader(FileStream wave)
    {
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(written * 4 + 36));
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(SampleRate * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)(written * 4));
        wave.Position = 0;
        wave.Write(header);
    }
}
