using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using SpireReplay.Media;

namespace ReplayHarness;

internal static class SyncAcceptance
{
    private const int ForeignFrequency = 3700;
    private const int Rate = 48000;
    private const int BlockSize = 480;
    private const int VideoRate = 30;
    private const int MaxOffsetMs = 100;
    private const int MaxDriftMs = 65;

    public static async Task RunAsync(NGame game)
    {
        var evidence = System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST");
        var ffmpeg = System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_FFMPEG");
        if (string.IsNullOrWhiteSpace(evidence) || !File.Exists(ffmpeg))
            throw new InvalidOperationException("Physical sync acceptance requires its evidence directory and FFmpeg.");
        if (!int.TryParse(System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_WITNESS_PID"), out int pid)
            || pid == System.Environment.ProcessId)
            throw new InvalidOperationException("Launch sync_witness.py to supply the unrelated process tone.");
        using var witness = Process.GetProcessById(pid);
        if (witness.HasExited) throw new InvalidOperationException("The unrelated process tone has stopped.");
        string output = Path.Combine(evidence, $"sync-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.mp4");

        using (AudioTestOutput.Open())
        using (var pulse = SyncPulse.Open())
        {
            var settings = SaveManager.Instance.SettingsSave;
            var layer = new CanvasLayer { Layer = 300 };
            game.GetTree().Root.AddChild(layer);
            var marker = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
            layer.AddChild(marker);
            marker.SetAnchorAndOffset(Side.Left, .43f, 0);
            marker.SetAnchorAndOffset(Side.Right, .57f, 0);
            marker.SetAnchorAndOffset(Side.Top, .43f, 0);
            marker.SetAnchorAndOffset(Side.Bottom, .57f, 0);
            try
            {
                // Silence the menu soundtrack so it cannot mask the deliberately isolated test signals.
                game.AudioManager.SetBgmVol(0f);
                game.AudioManager.SetSfxVol(0f);
                game.AudioManager.SetAmbienceVol(0f);
                await game.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                await VideoExporter.RecordAsync(output, async ct =>
                {
                    var clock = Stopwatch.StartNew();
                    async Task Until(int milliseconds)
                    {
                        var tree = game.GetTree();
                        while (clock.ElapsedMilliseconds < milliseconds)
                        {
                            ct.ThrowIfCancellationRequested();
                            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                        }
                    }
                    foreach (var onset in new[] { 1000, 2900, 5100 })
                    {
                        await Until(onset);
                        marker.Color = Colors.Lime;
                        pulse.Set(true);
                        await Until(onset + 330);
                        marker.Color = Colors.Black;
                        pulse.Set(false);
                    }
                    await Until(6100);
                }, CancellationToken.None);
            }
            finally
            {
                layer.Hide();
                layer.QueueFree();
                game.AudioManager.SetBgmVol(settings.VolumeBgm);
                game.AudioManager.SetSfxVol(settings.VolumeSfx);
                game.AudioManager.SetAmbienceVol(settings.VolumeAmbience);
            }
        }

        if (witness.HasExited) throw new InvalidDataException("The unrelated process exited during recording.");
        var (differences, drift) = await VerifyAsync(ffmpeg!, output, evidence, pid);
        GD.Print("REPLAY_VIDEO_SYNC_PASS offsets_ms=" + string.Join(", ", differences) + $" drift_ms={drift:F2}");
    }

    private static async Task<(double[] Differences, double Drift)> VerifyAsync(string ffmpeg, string output, string evidence, int pid)
    {
        // Use the muxed stream timestamps, including delayed first frames or AAC priming.
        var pixels = await Decode(ffmpeg, output, "-map", "0:v:0", "-an", "-vf",
            "fps=fps=30:start_time=0,crop=8:8:(iw-8)/2:(ih-8)/2,scale=1:1", "-pix_fmt", "rgb24", "-f", "rawvideo");
        var pcm = await Decode(ffmpeg, output, "-map", "0:a:0", "-vn", "-af",
            "aresample=48000:async=1:first_pts=0", "-ac", "1", "-ar", "48000", "-f", "s16le");
        var picture = new List<double>();
        bool green = false;
        for (int offset = 0; offset + 2 < pixels.Length; offset += 3)
        {
            bool next = pixels[offset] < 55 && pixels[offset + 1] > 190 && pixels[offset + 2] < 55;
            if (next && !green) picture.Add((offset / 3) * 1000d / VideoRate);
            green = next;
        }
        var own = FrequencyAmplitudes(pcm, SyncPulse.Frequency);
        var foreign = FrequencyAmplitudes(pcm, ForeignFrequency);
        // A sustained three-block threshold rejects AAC pre-echo and short onset transients.
        var sound = Onsets(own, .025);
        double witnessLevel = SustainedPeak(foreign);
        double ownLevel = SustainedPeak(own);
        var differences = picture.Zip(sound, (frame, tone) => Math.Round(tone - frame, 2)).ToArray();
        double drift = differences.Length == 0 ? 0 : differences.Max() - differences.Min();
        var report = new { mp4 = Path.GetFileName(output), witnessPid = pid, outputDevice = SyncPulse.Output,
            pictureMs = picture, soundMs = sound, audioMinusVideoMs = differences, driftMs = drift,
            gameHz = SyncPulse.Frequency, gameAmplitude = ownLevel, foreignHz = ForeignFrequency,
            foreignAmplitude = witnessLevel, maxOffsetMs = MaxOffsetMs, maxDriftMs = MaxDriftMs };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(evidence, "sync-measurement.json"), json);
        if (picture.Count != 3 || sound.Count != 3 || ownLevel < .06 || witnessLevel > .005
            || differences.Any(d => Math.Abs(d) > MaxOffsetMs) || drift > MaxDriftMs)
            throw new InvalidDataException("Physical A/V sync or process isolation failed: " + json);
        return (differences, drift);
    }

    private static double[] FrequencyAmplitudes(byte[] pcm, int frequency)
    {
        int blockCount = pcm.Length / (2 * BlockSize);
        var amplitude = new double[blockCount];
        double coefficient = 2 * Math.Cos(2 * Math.PI * frequency / Rate);
        for (int block = 0; block < blockCount; block++)
        {
            double previous = 0, older = 0;
            for (int i = 0; i < BlockSize; i++)
            {
                int offset = 2 * (block * BlockSize + i);
                var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset)) / 32768d;
                double current = sample + coefficient * previous - older;
                older = previous;
                previous = current;
            }
            double power = previous * previous + older * older - coefficient * previous * older;
            amplitude[block] = 2 * Math.Sqrt(Math.Max(0, power)) / BlockSize;
        }
        return amplitude;
    }

    private static double SustainedPeak(double[] amplitude)
    {
        double peak = 0;
        for (int i = 2; i < amplitude.Length; i++)
            peak = Math.Max(peak, Math.Min(amplitude[i - 2], Math.Min(amplitude[i - 1], amplitude[i])));
        return peak;
    }

    private static List<double> Onsets(double[] amplitude, double threshold)
    {
        var result = new List<double>();
        int above = 0;
        for (int i = 0; i < amplitude.Length; i++)
        {
            above = amplitude[i] >= threshold ? above + 1 : 0;
            if (above == 3) result.Add((i - 2) * BlockSize * 1000d / Rate);
        }
        return result;
    }

    private static async Task<byte[]> Decode(string ffmpeg, string input, params string[] options)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var arg in new[] { "-nostdin", "-loglevel", "error", "-copyts", "-i", input }
            .Concat(options).Append("pipe:1")) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not launch FFmpeg.");
        var errors = process.StandardError.ReadToEndAsync();
        using var decoded = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(decoded);
        await process.WaitForExitAsync();
        var message = await errors;
        if (process.ExitCode != 0) throw new InvalidDataException("FFmpeg: " + message);
        return decoded.ToArray();
    }
}
