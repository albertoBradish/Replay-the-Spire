using Godot;
using MegaCrit.Sts2.Core.Nodes;
using SpireReplay;
using SpireReplay.Playback;

namespace ReplayHarness;

internal static class UiAcceptance
{
    private static string PublishedFolder(Window window, string exports)
    {
        var status = ((Label)window.FindChild("ExportStatus", true, false)).Text;
        int offset = status.IndexOf(exports, StringComparison.OrdinalIgnoreCase);
        if (offset < 0 || !File.Exists(status[offset..]) || Path.GetFileName(status[offset..]) != "index.html")
            throw new InvalidDataException(status);
        return Path.GetDirectoryName(status[offset..])!;
    }

    internal static bool HasLoadedRuns(Window? window) =>
        window is { Visible: true } &&
        window.FindChild("RunPicker", true, false) is OptionButton { Disabled: false, ItemCount: > 0 };

    internal static async Task ForumAsync(string? runId = null)
    {
        var game = NGame.Instance!;
        var tree = game.GetTree();
        var root = tree.Root;
        Window? Library() => root.GetNodeOrNull<Window>("ReplayTheSpireLibrary");
        async Task Wait(Func<bool> predicate, string stage, double seconds = 15)
        {
            double until = Time.GetTicksMsec() + seconds * 1000;
            while (!predicate())
            {
                if (Time.GetTicksMsec() > until)
                    throw new TimeoutException($"{stage}: {ReplayPlayer.Status} / {(Library()?.FindChild("ExportStatus", true, false) as Label)?.Text}");
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
        }
        if (Library() is { } existing)
        {
            existing.EmitSignal(Window.SignalName.CloseRequested);
            await Wait(() => Library() == null, "close previous library");
        }
        var runs = ReplayStore.ListRuns().OrderByDescending(r => r.StartedUtc).ToList();
        var run = runId == null ? runs.First(r => r.Rooms.Any(x => x.HasCombat) &&
            r.Rooms.Any(x => !x.HasCombat && x.HasCheckpoint && ReplayStore.LoadEvents(r.Id, x.Index).Any(e => e.Kind == "event"))) : runs.Single(x => x.Id == runId);
        game.MainMenu!.GetNode<Button>("ReplayTheSpireEntry").EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(() => HasLoadedRuns(Library()), "load forum library");
        var window = Library()!;
        try
        {
            var picker = (OptionButton)window.FindChild("RunPicker", true, false);
            int index = runs.IndexOf(run);
            picker.Select(index);
            picker.EmitSignal(OptionButton.SignalName.ItemSelected, (long)index);
            var action = (Button)window.FindChild("CaptureRunAlbum", true, false);
            if (action.Disabled) throw new InvalidDataException("Native whole-run photo journal disabled");
            string exports = ReplayStore.ExportsDirectory;

            var speed = ReplayPlayer.Speed;
            var timing = ReplayPlayer.OriginalTiming;
            Vector2I sourceSize;
            using (var source = root.GetTexture().GetImage()) sourceSize = source.GetSize();
            action.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(() => !window.Visible, "start whole-run album");
            await Wait(() => window.Visible && !ReplayPlayer.IsActive, "finish whole-run album", Math.Max(240, run.Rooms.Count * 45));
            if (ReplayPlayer.Speed != speed || ReplayPlayer.OriginalTiming != timing)
                throw new InvalidDataException("Album changed the viewer's playback controls");
            var folder = PublishedFolder(window, exports);
            var html = await File.ReadAllTextAsync(Path.Combine(folder, "index.html"));
            var post = await File.ReadAllTextAsync(Path.Combine(folder, "index.txt"));
            int combat = 0, noncombat = 0, annotated = 0;
            var entries = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal);
            if (!entries.SequenceEqual(new[] { "images", "index.html", "index.txt" }))
                throw new InvalidDataException("Unexpected files in the published album.");
            int roomNumber = 0;
            foreach (var entry in run.Rooms)
            {
                roomNumber++;
                if (!post.Contains($"第 {entry.Floor} 层"))
                    throw new InvalidDataException($"Journal skipped room {entry.Index}");
                var tracks = ReplayStore.LoadEvents(run.Id, entry.Index);
                var photos = Directory.GetFiles(Path.Combine(folder, "images"), $"{roomNumber:D3}-*").Order(StringComparer.Ordinal).ToArray();
                if (photos.Length > (entry.HasCombat ? 3 : 2)) throw new InvalidDataException("Too many representative frames");
                if (entry.HasCombat) combat += photos.Length;
                else if (entry.HasCheckpoint) noncombat += photos.Length;
                for (int i = 0; i < photos.Length; i++)
                {
                    string path = photos[i];
                    if (Path.GetFileName(path) != $"{roomNumber:D3}-F{entry.Floor:D2}-{i + 1:D2}.jpg")
                        throw new InvalidDataException("Journal photos must be consecutive JPEGs");
                    using var encoded = File.OpenRead(path);
                    if (encoded.ReadByte() != 0xFF || encoded.ReadByte() != 0xD8) throw new InvalidDataException("JPEG header mismatch");
                    using var image = Image.LoadFromFile(path);
                    string rel = "images/" + Path.GetFileName(path);
                    if (image.IsEmpty() || image.GetSize() != sourceSize || !html.Contains(rel) || !post.Contains(rel))
                        throw new InvalidDataException("Missing journal image or caption: " + rel);
                    string line = post.Split('\n').Single(text => text.Contains(rel));
                    if (tracks.Any(e => e.Card.Length > 0 && e.Kind is "damage" or "block" && line.Contains(e.Label)) &&
                        (line.Contains("实际扣血") || line.Contains("获得格挡"))) annotated++;
                    var levels = from y in Enumerable.Range(1, 7) from x in Enumerable.Range(1, 11)
                        select image.GetPixel(x * image.GetWidth() / 12, y * image.GetHeight() / 8).Luminance;
                    if (levels.Max() - levels.Min() < .07f) throw new InvalidDataException("Blank native room frame: " + rel);
                }
            }
            if (combat == 0 || noncombat == 0 || annotated == 0)
                throw new InvalidDataException($"Mixed journey lacked frames or card effect captions: combat={combat} noncombat={noncombat} annotated={annotated}");
            GD.Print($"REPLAY_FORUM_ALBUM_PASS rooms={run.Rooms.Count} combat-frames={combat} room-frames={noncombat} annotated={annotated} source={sourceSize} {folder}");
        }
        finally
        {
            if (root.FindChild("CancelAlbum", true, false) is Button { Visible: true } cancel)
            {
                cancel.EmitSignal(BaseButton.SignalName.Pressed);
                await Wait(() => !ReplayPlayer.IsActive && window.Visible, "cancel whole-run album", 30);
            }
            window.EmitSignal(Window.SignalName.CloseRequested);
            await Wait(() => Library() == null, "close forum library");
        }
    }

    internal static async Task AlbumAsync(string runId, int roomIndex)
    {
        var game = NGame.Instance!;
        var tree = game.GetTree();
        var root = tree.Root;
        Window? Library() => root.GetNodeOrNull<Window>("ReplayTheSpireLibrary");
        async Task Wait(Func<bool> ready, string stage, double seconds = 12)
        {
            double deadline = Time.GetTicksMsec() + seconds * 1000;
            while (!ready())
            {
                if (Time.GetTicksMsec() > deadline)
                {
                    var status = Library()?.FindChild("ExportStatus", true, false) as Label;
                    throw new TimeoutException($"{stage}: {status?.Text} / {ReplayPlayer.Status}");
                }
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
        }
        async Task CloseLibrary()
        {
            if (Library() is not { } window) return;
            window.EmitSignal(Window.SignalName.CloseRequested);
            await Wait(() => Library() == null, "close library");
        }

        await CloseLibrary();
        var menu = game.MainMenu ?? throw new InvalidOperationException("Main menu unavailable");
        menu.GetNode<Button>("ReplayTheSpireEntry").EmitSignal(BaseButton.SignalName.Pressed);
        // A player can close the window while its first catalog read is still outstanding.
        await CloseLibrary();
        menu.GetNode<Button>("ReplayTheSpireEntry").EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(() => HasLoadedRuns(Library()), "load reopened library");
        var browser = Library()!;
        try
        {
            var run = ReplayStore.ListRuns().Single(r => r.Id == runId);
            var runs = (OptionButton)browser.FindChild("RunPicker", true, false);
            var options = ReplayStore.ListRuns().OrderByDescending(r => r.StartedUtc).ToList();
            int runChoice = options.FindIndex(r => r.Id == runId);
            if (runs.Selected != runChoice)
            {
                runs.Select(runChoice);
                runs.EmitSignal(OptionButton.SignalName.ItemSelected, (long)runChoice);
            }
            var rooms = (OptionButton)browser.FindChild("RoomPicker", true, false);
            int target = run.Rooms.FindIndex(r => r.Index == roomIndex);
            if (rooms.Selected != target)
                throw new InvalidDataException($"Library opened room {rooms.Selected}, expected playable room {target}");
            if (!browser.Borderless) throw new InvalidDataException("Duplicate library title bar");
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            var metadata = (Label)browser.FindChild("RunMetadata", true, false);
            long bytes = new DirectoryInfo(Path.Combine(ReplayStore.LibraryDirectory, runId)).EnumerateFiles().Sum(f => f.Length);
            await Wait(() => metadata.TooltipText.Contains($"({bytes:N0} B)"), "storage metadata");
            var originals = (Button)browser.FindChild("OpenRunFolder", true, false);
            if (!originals.IsVisibleInTree() || originals.Disabled) throw new InvalidDataException("Missing local run folder link");
            await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using (var shot = browser.GetTexture().GetImage())
            {
                if (root.Size == new Vector2I(1280, 720) && (shot.GetWidth() is < 980 or > 1150 || shot.GetHeight() is < 580 or > 680))
                    throw new InvalidDataException($"Unreadable library size {shot.GetSize()}, canvas {root.GetVisibleRect().Size}, subwindow {browser.Size}");
                shot.SavePng(Path.Combine(System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST")!, "library-720p.png"));
            }
            var stats = (Button)browser.FindChild("CardTotals", true, false);
            stats.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(() => browser.FindChild("CardTotalsList", true, false) is ItemList { ItemCount: > 0 }, "card totals");
            var totals = (ItemList)browser.FindChild("CardTotalsList", true, false);
            totals.Select(0);
            totals.EmitSignal(ItemList.SignalName.ItemSelected, 0L);
            var drilldown = (Button)browser.FindChild("ViewCardDetails", true, false);
            if (drilldown.Disabled || browser.FindChild("CardMomentList", true, false) != null)
                throw new InvalidDataException("Card selection opened before activation");
            drilldown.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(() => browser.FindChild("CardMomentList", true, false) is ItemList { ItemCount: > 0 }, "card contributions");
            ((Button)browser.FindChild("BackToCards", true, false)).EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(() => browser.FindChild("CardTotalsList", true, false) != null, "return to cards");
            ((Button)browser.FindChild("BackToTimeline", true, false)).EmitSignal(BaseButton.SignalName.Pressed);
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            var capture = (Button)browser.FindChild("CaptureAlbum", true, false);
            var bounds = browser.GetVisibleRect();
            foreach (string name in new[] { "CaptureAlbum", "CaptureRunAlbum", "ExportRoomVideo", "JumpToMoment", "RunMetadata", "OpenRunFolder", "PlayRoom", "RoomPicker" })
                if (browser.FindChild(name, true, false) is not Control widget || !widget.IsVisibleInTree() || !bounds.Encloses(widget.GetGlobalRect()))
                    throw new InvalidDataException("1280×720 library control not reachable: " + name);
            if (capture.Disabled) throw new InvalidDataException("Capture button disabled");

            capture.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(() => !browser.Visible, "start album");
            await Wait(() => browser.Visible && !ReplayPlayer.IsActive, "finish album playback", 120);
            var status = (Label)browser.FindChild("ExportStatus", true, false);
            var open = (Button)browser.FindChild("OpenExportFolder", true, false);
            await Wait(() => open.Visible, "album export: " + status.Text);
            string folder = PublishedFolder(browser, ReplayStore.ExportsDirectory);
            string htmlPath = Path.Combine(folder, "index.html");
            string html = await File.ReadAllTextAsync(htmlPath);
            var pngs = Directory.GetFiles(Path.Combine(folder, "images"), "*.jpg");
            if (pngs.Length == 0) throw new InvalidDataException("Missing representative room photos");
            foreach (string png in pngs)
            {
                using var image = Image.LoadFromFile(png);
                if (image.IsEmpty() || image.GetWidth() < 640 || image.GetHeight() < 360 || !html.Contains(Path.GetFileName(png)))
                    throw new InvalidDataException($"Invalid gallery capture: {png}");
                var levels = from y in Enumerable.Range(1, 7) from x in Enumerable.Range(1, 11)
                    select image.GetPixel(x * image.GetWidth() / 12, y * image.GetHeight() / 8).Luminance;
                if (levels.Max() - levels.Min() < 0.07f) throw new InvalidDataException($"Blank gallery capture: {png}");
            }
            GD.Print($"REPLAY_ALBUM_PASS {pngs.Length} native photos and {htmlPath}");
            var timeline = (ItemList)browser.FindChild("FloorTimeline", true, false);
            var events = ReplayStore.LoadEvents(run.Id, roomIndex);
            var filter = (OptionButton)browser.FindChild("TimelineFilter", true, false);
            filter.Select(2);
            filter.EmitSignal(OptionButton.SignalName.ItemSelected, 2L);
            var measured = events.Where(e => e.DecisionSeconds.HasValue).OrderByDescending(e => e.DecisionSeconds).ToList();
            if (measured.Count > 0 && (timeline.ItemCount != measured.Count ||
                !Equals(events[timeline.GetItemMetadata(0).AsInt32()], measured[0])))
                throw new InvalidDataException("Longest decision filter did not preserve room indices");
            filter.Select(0);
            filter.EmitSignal(OptionButton.SignalName.ItemSelected, 0L);
            int turn = events.FindIndex(e => e.Kind == "turn" && e.Turn == 1);
            if (turn < 0) throw new InvalidDataException("No first turn to seek");
            timeline.Select(turn);
            timeline.EmitSignal(ItemList.SignalName.ItemSelected, (long)turn);
            var seek = (Button)browser.FindChild("JumpToMoment", true, false);
            if (seek.Disabled) throw new InvalidDataException("Seek did not enable");
            seek.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(() => ReplayPlayer.IsPaused && !ReplayPlayer.IsSeeking && !ReplayPlayer.IsFinished, "seek from timeline", 45);
            var toolbar = root.GetNode<CanvasLayer>("ReplayTheSpireControls");
            Button ControlButton(string name) => (Button)toolbar.FindChild(name, true, false);
            var pause = ControlButton("PauseReplay");
            await Wait(() => !pause.Disabled && pause.Text is "继续" or "Resume", "resume control after seek");
            foreach (string name in new[] { "PauseReplay", "StepReplay", "PreviousTurn", "NextTurn", "BackToLibrary" })
                if (!ControlButton(name).IsVisibleInTree() || !root.GetVisibleRect().Encloses(ControlButton(name).GetGlobalRect()))
                    throw new InvalidDataException("Replay control not reachable: " + name);
            float pausePixels = pause.GetGlobalRect().Size.Y * root.Size.Y / root.GetVisibleRect().Size.Y;
            if (pausePixels < 30) throw new InvalidDataException($"Small playback control: {pausePixels}px");
            await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using (var shot = root.GetTexture().GetImage())
                shot.SavePng(Path.Combine(System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST")!, "toolbar-paused.png"));
            ControlButton("CollapseReplay").EmitSignal(BaseButton.SignalName.Pressed);
            if (pause.IsVisibleInTree() || !ControlButton("BackToLibrary").IsVisibleInTree()) throw new InvalidDataException("Toolbar did not collapse");
            ControlButton("CollapseReplay").EmitSignal(BaseButton.SignalName.Pressed);
            if (!pause.IsVisibleInTree()) throw new InvalidDataException("Toolbar did not expand");
            ControlButton("BackToLibrary").EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(() => Library()?.Visible == true && !ReplayPlayer.IsActive, "return to selected run", 45);
            GD.Print($"REPLAY_LIBRARY_CONTROLS_PASS storage_bytes={bytes}");
        }
        finally
        {
            if (root.FindChild("CancelAlbum", true, false) is Button { Visible: true } cancel)
            {
                cancel.EmitSignal(BaseButton.SignalName.Pressed);
                await Wait(() => !ReplayPlayer.IsActive, "stop album", 30);
            }
            await CloseLibrary();
        }
    }
}
