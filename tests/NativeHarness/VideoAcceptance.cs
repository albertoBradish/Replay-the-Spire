using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using SpireReplay;
using SpireReplay.Media;
using SpireReplay.Playback;

namespace ReplayHarness;

internal static class VideoAcceptance
{
    internal static async Task MutedAsync(string runId, int roomIndex)
    {
        if (SaveManager.Instance.SettingsSave.VolumeMaster != 0) throw new InvalidOperationException("Test requires the muted virtual audio route");
        await ExportAsync(runId, roomIndex, mutedChoice: "CancelAudioChoice");
        await ExportAsync(runId, roomIndex, mutedChoice: "ExportSilent");
        await ExportAsync(runId, roomIndex, mutedChoice: "ExportWithSound");
        if (SaveManager.Instance.SettingsSave.VolumeMaster != 0) throw new InvalidDataException("Temporary film volume persisted");
        GD.Print("REPLAY_MUTED_VIDEO_PASS");
    }

    internal static async Task<string?> ExportAsync(string runId, int roomIndex, bool wholeRun = false, bool testCancel = false, int height = 720, string? mutedChoice = null)
    {
        if (!VideoExporter.EncoderAvailable) throw new InvalidOperationException("Missing test encoder");
        var tree = NGame.Instance!.GetTree();
        var root = tree.Root;
        Window? Library() => root.GetNodeOrNull<Window>("ReplayTheSpireLibrary");
        Window? Progress() => root.GetNodeOrNull<Window>("ReplayFilmProgress");
        async Task Wait(Func<bool> done, string name, double seconds = 15)
        {
            double deadline = Time.GetTicksMsec() + seconds * 1000;
            while (!done())
            {
                if (Time.GetTicksMsec() > deadline)
                {
                    var label = Library()?.FindChild("ExportStatus", true, false) as Label;
                    throw new TimeoutException($"{name}: {label?.Text} / {ReplayPlayer.Status}");
                }
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
        }
        if (Library() is { } old)
        {
            old.EmitSignal(Window.SignalName.CloseRequested);
            await Wait(() => Library() == null, "close previous library");
        }
        NGame.Instance.MainMenu!.GetNode<Button>("ReplayTheSpireEntry").EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(() => Library()?.Visible == true, "open video library");
        var library = Library()!;
        try
        {
            var catalog = ReplayStore.ListRuns().OrderByDescending(r => r.StartedUtc).ToList();
            int runChoice = catalog.FindIndex(r => r.Id == runId);
            var runs = (OptionButton)library.FindChild("RunPicker", true, false);
            runs.Select(runChoice);
            runs.EmitSignal(OptionButton.SignalName.ItemSelected, (long)runChoice);
            int roomChoice = catalog[runChoice].Rooms.FindIndex(r => r.Index == roomIndex);
            var rooms = (OptionButton)library.FindChild("RoomPicker", true, false);
            rooms.Select(roomChoice);
            rooms.EmitSignal(OptionButton.SignalName.ItemSelected, (long)roomChoice);
            string folder = Path.Combine(ReplayStore.ExportsDirectory, "video");
            var before = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.mp4").ToHashSet() : [];
            var start = (Button)library.FindChild("ExportRoomVideo", true, false);
            if (start.Disabled) throw new InvalidDataException("Video action disabled for compatible run");
            if (height == 1080)
            {
                var hd = (PopupMenu)library.FindChild("HighQualityVideo", true, false);
                hd.EmitSignal(PopupMenu.SignalName.IdPressed, wholeRun ? 1L : 0L);
            }
            else if (wholeRun)
            {
                var exports = (MenuButton)library.FindChild("ExportOptions", true, false);
                exports.GetPopup().EmitSignal(PopupMenu.SignalName.IdPressed, 3L);
            }
            else start.EmitSignal(BaseButton.SignalName.Pressed);
            var volume = library.GetNodeOrNull<Window>("MutedVideo");
            if (mutedChoice != null)
            {
                if (volume == null || !volume.Visible || Progress() != null || VideoExporter.IsRecording)
                    throw new InvalidDataException("Missing muted-audio selection before recording");
                ((Button)volume.FindChild(mutedChoice, true, false)).EmitSignal(BaseButton.SignalName.Pressed);
                if (mutedChoice == "CancelAudioChoice")
                {
                    await Wait(() => library.GetNodeOrNull<Window>("MutedVideo") == null, "dismiss audio dialog");
                    if (!library.Visible || Progress() != null || VideoExporter.IsRecording) throw new InvalidDataException("Cancel started an export");
                    return null;
                }
            }
            else if (volume != null) throw new InvalidDataException("Normal video QA requires nonzero virtual test volume");
            await Wait(() => !library.Visible && Progress() != null, "start MP4");
            if (Progress()!.IsEmbedded() && Progress()!.Visible) throw new InvalidDataException("Progress HUD appears inside the film");
            if (testCancel)
            {
                await Wait(() => VideoExporter.IsRecording, "start cancellable encoder");
                ((Button)Progress()!.FindChild("CancelVideoExport", true, false)).EmitSignal(BaseButton.SignalName.Pressed);
            }
            double exportDeadline = wholeRun ? Math.Max(300, catalog[runChoice].Rooms.Count * 60) : 120;
            await Wait(() => library.Visible && !VideoExporter.IsRecording && !ReplayPlayer.IsActive, "finish video", exportDeadline);
            var created = Directory.GetFiles(folder, "*.mp4").Where(p => !before.Contains(p)).ToArray();
            if (testCancel)
            {
                if (created.Length != 0) throw new InvalidDataException("Cancelled video published a partial MP4");
                GD.Print("REPLAY_VIDEO_CANCEL_PASS");
                return null;
            }
            if (created.Length != 1)
            {
                string why = ((Label)library.FindChild("ExportStatus", true, false)).Text;
                throw new InvalidDataException($"Expected one new MP4; {why}");
            }
            var path = created[0];
            string ffmpeg = System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_FFMPEG")
                ?? throw new InvalidOperationException("Set SPIRE_REPLAY_FFMPEG for A/V decoding in native acceptance");
            string audio = await Decode(ffmpeg, path, "-map", "0:a:0", "-af", "volumedetect", "-f", "null", "-");
            var peak = Regex.Match(audio, @"max_volume:\s*(-?[\d.]+) dB");
            var rms = Regex.Match(audio, @"mean_volume:\s*(-?[\d.]+) dB");
            if (!peak.Success || !rms.Success) throw new InvalidDataException("Missing decoded volume measurement");
            var amplitude = double.Parse(peak.Groups[1].Value, CultureInfo.InvariantCulture);
            var mean = double.Parse(rms.Groups[1].Value, CultureInfo.InvariantCulture);
            // FMOD and AAC conversion can leave one- and two-LSB dither in a muted track.
            if (mutedChoice == "ExportSilent" ? amplitude > -82 || mean > -89 : amplitude <= -70)
                throw new InvalidDataException($"Unexpected audio for {mutedChoice ?? "normal export"}: {peak.Value}, {rms.Value}");
            string video = await Decode(ffmpeg, path, "-map", "0:v:0", "-frames:v", "1", "-f", "null", "-");
            if (!Regex.IsMatch(video, @"Video:\s*h264") || !video.Contains("30 fps") ||
                !Regex.IsMatch(video, @"Audio:\s*aac"))
                throw new InvalidDataException("Expected H.264 at 30 fps and AAC in exported file");
            if (height == 1080 && !video.Contains("1920x1080"))
                throw new InvalidDataException("HD test expected a 1920x1080 video frame");
            GD.Print($"REPLAY_VIDEO_PASS {(wholeRun ? "run" : "floor")} {peak.Value} {new FileInfo(path).Length} {path}");
            return path;
        }
        finally
        {
            if (Progress()?.FindChild("CancelVideoExport", true, false) is Button { Disabled: false } stop)
            {
                stop.EmitSignal(BaseButton.SignalName.Pressed);
                await Wait(() => !VideoExporter.IsRecording, "stop encoder", 30);
            }
            if (Library() is { } open)
            {
                open.EmitSignal(Window.SignalName.CloseRequested);
                await Wait(() => Library() == null, "close video library");
            }
        }
    }

    internal static async Task HdAsync(string runId, int roomIndex)
    {
        var tree = NGame.Instance!.GetTree();
        var window = tree.Root;
        var mode = window.ContentScaleMode;
        var aspect = window.ContentScaleAspect;
        var stretch = window.ContentScaleStretch;
        var factor = window.ContentScaleFactor;
        var baseSize = window.ContentScaleSize;
        var desktopSize = window.Size;
        async Task Draw()
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
        try
        {
            window.ContentScaleStretch = Window.ContentScaleStretchEnum.Fractional;
            window.ContentScaleFactor = 1;
            window.ContentScaleSize = new Vector2I(1280, 720);
            window.ContentScaleAspect = Window.ContentScaleAspectEnum.Keep;
            window.ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
            await Draw();
            var markers = new CanvasLayer { Layer = 200 };
            window.AddChild(markers);
            // A single texture has a fixed, unambiguous normalized coordinate system.
            // This avoids Control anchor/minimum-size behavior when the root window is resized.
            using var pattern = Image.CreateEmpty(100, 100, false, Image.Format.Rgba8);
            pattern.Fill(Colors.Transparent);
            pattern.FillRect(new Rect2I(17, 22, 6, 6), Colors.Red);
            pattern.FillRect(new Rect2I(77, 72, 6, 6), Colors.Lime);
            var sprite = new Sprite2D
            {
                Texture = ImageTexture.CreateFromImage(pattern), Centered = false,
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest, Scale = window.GetVisibleRect().Size / 100f
            };
            markers.AddChild(sprite);
            void CheckFrame(string stage, Vector2I expected)
            {
                using var img = window.GetTexture().GetImage();
                var size = img.GetSize();
                if (size != expected) throw new InvalidDataException($"GPU frame {size}, expected {expected}");
                var red = img.GetPixel((int)(size.X * .2f), (int)(size.Y * .25f));
                var green = img.GetPixel((int)(size.X * .8f), (int)(size.Y * .75f));
                if (red.R < .8f || red.G > .2f || green.G < .8f || green.R > .2f)
                {
                    string? directory = System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST");
                    if (!string.IsNullOrWhiteSpace(directory)) img.SavePng(Path.Combine(directory, $"hd-{stage}-failure.png"));
                    throw new InvalidDataException($"{stage} {size} shifted UI anchors: {red}, {green}; logical {window.GetVisibleRect()}, sprite {sprite.GlobalTransform}, viewport {window.GetStretchTransform()}");
                }
            }
            try
            {
                await Draw();
                var logical = window.GetVisibleRect();
                CheckFrame("baseline", new Vector2I(1280, 720));
                using (var target = await VideoRenderTarget.EnsureAsync(window, 1080, CancellationToken.None))
                {
                    CheckFrame("supersample", new Vector2I(1920, 1080));
                    if (window.Size != desktopSize || window.GetVisibleRect() != logical)
                        throw new InvalidDataException("HD render shifted OS window or UI coordinates");
                    GD.Print($"REPLAY_HD_SOURCE {target.OriginalSize} -> {target.SourceSize} logical={logical.Size} OS={window.Size}");
                }
                await Draw();
                CheckFrame("restored", new Vector2I(1280, 720));
            }
            finally { markers.Hide(); markers.QueueFree(); }
            await Draw();
            await ExportAsync(runId, roomIndex, height: 1080);
            await Draw();
            using var original = window.GetTexture().GetImage();
            if (original.GetSize() != new Vector2I(1280, 720) || window.Size != desktopSize)
                throw new InvalidDataException("Video export left its render size behind");
            GD.Print("REPLAY_HD_RESTORE_PASS");
        }
        finally
        {
            window.ContentScaleMode = mode;
            window.ContentScaleSize = baseSize;
            window.ContentScaleFactor = factor;
            window.ContentScaleAspect = aspect;
            window.ContentScaleStretch = stretch;
        }
    }

    private static async Task<string> Decode(string executable, string source, params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-nostdin", "-hide_banner", "-i", source }.Concat(args)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Failed to start FFmpeg for acceptance");
        string report = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidDataException(report);
        return report;
    }
}
