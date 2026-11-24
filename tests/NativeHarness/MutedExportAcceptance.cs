using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using SpireReplay;
using SpireReplay.Media;
using SpireReplay.Playback;

namespace ReplayHarness;

internal static class MutedExportAcceptance
{
    internal static async Task RunAsync(NGame game)
    {
        var evidence = System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST")!;
        var encoder = System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_FFMPEG")!;
        var run = ReplayStore.ListRuns().First(r => r.Seed == "REPLAYPROOF");
        var room = ReplayStore.LoadRoom(run.Id, run.Rooms.Last(r => r.HasCombat).Index);
        var tree = game.GetTree();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        string Output(string suffix) => Path.Combine(evidence, $"muted-mix-{stamp}-{suffix}.mp4");

        using var route = AudioTestOutput.Open(audible: false);
        var bus = Engine.GetSingleton("FmodServer").Call("get_bus", "bus:/master").AsGodotObject();
        async Task CheckMute()
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (SaveManager.Instance.SettingsSave.VolumeMaster != 0 || Math.Abs(bus.Get("volume").AsDouble()) > .0001)
                throw new InvalidDataException("Game master volume was not restored to mute.");
        }
        async Task Play(CancellationToken token)
        {
            try
            {
                await ReplayPlayer.PlayAsync(room.Combat!, 0, room.Events);
                ReplayPlayer.SetPaused(false);
                var elapsed = Stopwatch.StartNew();
                while (!ReplayPlayer.IsFinished)
                {
                    token.ThrowIfCancellationRequested();
                    if (elapsed.Elapsed.TotalSeconds > 55) throw new TimeoutException(ReplayPlayer.Status);
                    await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                }
                if (!ReplayPlayer.IsVerified) throw new InvalidDataException(ReplayPlayer.Status);
            }
            finally { if (ReplayPlayer.IsActive) await ReplayPlayer.StopAsync(); }
        }

        await CheckMute();
        string silent = await VideoExporter.RecordAsync(Output("default"), Play, CancellationToken.None);
        await CheckMute();
        string audible = await VideoExporter.RecordAsync(Output("confirmed"), Play, CancellationToken.None, targetHeight: 720, audioGain: 1f);
        await CheckMute();
        string failed = Output("failure");
        const string failure = "muted-mix forced media failure";
        try
        {
            await VideoExporter.RecordAsync(failed, _ => throw new InvalidOperationException(failure),
                CancellationToken.None, targetHeight: 720, audioGain: 1f);
            throw new InvalidDataException("Expected the export's playback delegate to fail.");
        }
        catch (InvalidOperationException ex) when (ex.Message == failure) { }
        await CheckMute();
        if (File.Exists(failed) || VideoExporter.IsRecording) throw new InvalidDataException("Failed export left an output or busy encoder.");
        double mutedPeak = await Peak(encoder, silent);
        double confirmedPeak = await Peak(encoder, audible);
        string report = JsonSerializer.Serialize(new { mutedPeakDb = mutedPeak, confirmedPeakDb = confirmedPeak,
            savedMasterAfter = SaveManager.Instance.SettingsSave.VolumeMaster, failureRestored = true,
            silent = Path.GetFileName(silent), audible = Path.GetFileName(audible) });
        await File.WriteAllTextAsync(Path.Combine(evidence, "muted-mix-measurement.json"), report);
        if (mutedPeak > -75 || confirmedPeak < -55) throw new InvalidDataException(report);
        GD.Print("REPLAY_VIDEO_MUTED_MIX_PASS " + report);
    }

    private static async Task<double> Peak(string ffmpeg, string path)
    {
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-nostdin", "-hide_banner", "-i", path, "-map", "0:a:0", "-af", "volumedetect", "-f", "null", "-" })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not start FFmpeg.");
        string log = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var match = Regex.Match(log, @"max_volume:\s*(-?[\d.]+) dB");
        if (process.ExitCode != 0 || !match.Success) throw new InvalidDataException(log);
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
