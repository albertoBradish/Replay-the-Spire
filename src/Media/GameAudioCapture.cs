using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SpireReplay.Media;

/// <summary>Captures the Windows game's 48 kHz stereo PCM for streaming or WAV output.</summary>
public sealed class GameAudioCapture : IDisposable
{
    public int SampleRate => 48000;
    public int Channels => 2;
    public long FramesWritten => Interlocked.Read(ref written);
    public long StartedTimestamp { get; private set; }

    private readonly string? path;
    private readonly Action<byte[], int, int>? output;
    private readonly Action<Exception>? onFailure;
    private readonly ManualResetEvent stop = new(false);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread worker;
    private readonly object lifetime = new();
    private long written;
    private long finalFrames = -1;
    private Exception? failure;
    private bool disposed;

    [SupportedOSPlatform("windows10.0.19041")]
    private GameAudioCapture(string? path, Action<byte[], int, int>? output, Action<Exception>? onFailure)
    {
        this.path = path;
        this.output = output;
        this.onFailure = onFailure;
        worker = new Thread(Record) { IsBackground = true, Name = "Replay the Spire audio" };
    }

    public static GameAudioCapture Start(string wavPath) => Start(Path.GetFullPath(wavPath), null, null);

    internal static GameAudioCapture Start(Action<byte[], int, int> output, Action<Exception>? onFailure = null) =>
        Start(null, output, onFailure);

    private static GameAudioCapture Start(string? path, Action<byte[], int, int>? output, Action<Exception>? onFailure)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("请在 64 位 Windows 10 2004（build 19041）或更高版本上导出游戏音轨。");
        var capture = new GameAudioCapture(path, output, onFailure);
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

    public void Stop() => Stop(-1);

    internal void Stop(long frameCount)
    {
        lock (lifetime)
        {
            if (disposed) return;
            disposed = true;
            Volatile.Write(ref finalFrames, frameCount);
            stop.Set();
            worker.Join();
            stop.Dispose();
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public void Dispose() => Stop();

    [SupportedOSPlatform("windows10.0.19041")]
    private void Record()
    {
        AudioInterfaces.IAudioClient? client = null;
        AudioInterfaces.ICaptureClient? packets = null;
        bool started = false;
        try
        {
            using var signal = new AutoResetEvent(false);
            using var wave = path == null ? null : new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
            wave?.Write(new byte[44]);
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
            while (WaitHandle.WaitAny(waits, output == null ? Timeout.Infinite : 100) != 0)
            {
                Drain(packets, wave, scratch, silence, stopping: false);
                // A completely silent session can emit no WASAPI packets. Keep both inputs moving,
                // leaving 100 ms for real packets whose delivery lagged their QPC timestamp.
                if (output != null)
                {
                    long clock = (long)(Stopwatch.GetElapsedTime(StartedTimestamp).TotalSeconds * SampleRate);
                    long gap = clock - written - SampleRate / 10;
                    if (gap > 0) Pad(wave, gap, silence);
                }
            }
            var end = Stopwatch.GetTimestamp();
            Marshal.ThrowExceptionForHR(client.Stop());
            started = false;
            Drain(packets, wave, scratch, silence, stopping: true);
            var endingFrame = Volatile.Read(ref finalFrames);
            if (endingFrame < 0) endingFrame = (long)Math.Round(Stopwatch.GetElapsedTime(StartedTimestamp, end).TotalSeconds * SampleRate);
            // A silent process may supply no packets. Keep silence on the same real-time clock as the video.
            if (written < endingFrame) Pad(wave, endingFrame - written, silence);
            else if (written > endingFrame && wave != null)
            {
                wave.SetLength(44 + endingFrame * 4);
                Interlocked.Exchange(ref written, endingFrame);
            }
            if (wave != null) WriteHeader(wave);
        }
        catch (Exception error)
        {
            Volatile.Write(ref failure, error);
            ready.TrySetException(error);
            try { onFailure?.Invoke(error); }
            catch (Exception notificationError) { Volatile.Write(ref failure, new AggregateException(error, notificationError)); }
        }
        finally
        {
            if (started) client?.Stop();
            if (packets != null) Marshal.ReleaseComObject(packets);
            if (client != null) Marshal.ReleaseComObject(client);
        }
    }

    [SupportedOSPlatform("windows10.0.19041")]
    private void Drain(AudioInterfaces.ICaptureClient packets, FileStream? wave, byte[] scratch, byte[] silence, bool stopping)
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
            int remaining = (int)frames - skipFrames;
            long limit = Volatile.Read(ref finalFrames);
            if (limit >= 0) remaining = (int)Math.Clamp(limit - written, 0, remaining);
            CheckSize(written + remaining);
            Write(wave, scratch, skipFrames * 4, remaining * 4);
            Interlocked.Add(ref written, remaining);
        }
    }

    private void Write(FileStream? wave, byte[] bytes, int offset, int count)
    {
        if (wave != null) wave.Write(bytes, offset, count);
        else output!(bytes, offset, count);
    }

    private void Pad(FileStream? wave, long count, byte[] silence)
    {
        long limit = Volatile.Read(ref finalFrames);
        if (limit >= 0) count = Math.Min(count, Math.Max(0, limit - written));
        CheckSize(written + count);
        while (count > 0)
        {
            int frames = (int)Math.Min(count, silence.Length / 4);
            Write(wave, silence, 0, frames * 4);
            Interlocked.Add(ref written, frames);
            count -= frames;
        }
    }

    private void CheckSize(long frames)
    {
        if (frames < 0 || (path != null && frames > (uint.MaxValue - 36) / 4))
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
