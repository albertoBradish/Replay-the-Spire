using Godot;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using SpireReplay.Playback;
using System.Diagnostics;
using static SpireReplay.Interface.ReplayStyle;

namespace SpireReplay.Interface;

internal sealed class ReplayToolbar
{
    private readonly SceneTree tree;
    private readonly RunInfo run;
    private RoomInfo room;
    private readonly CanvasLayer layer = new() { Layer = 101, ProcessMode = Node.ProcessModeEnum.Always };
    private readonly Label position = Text("", 14);
    private readonly Label note = Text("", 13, true);
    private readonly Button pause;
    private readonly Button step;
    private readonly Button previous;
    private readonly Button next;
    private readonly Button snapshot;
    private readonly Button exit;
    private readonly Button collapse;
    private readonly Button cancelAlbum;
    private readonly OptionButton speed = new();
    private readonly OptionButton floors = new();
    private readonly CheckButton continuousPlay = new();
    private readonly PanelContainer summary = Panel(Ink, 16);
    private readonly VBoxContainer summaryBody = Column();
    private readonly PanelContainer panel = Panel(Ink, 8);
    private readonly VBoxContainer sheet = Column(4);
    private readonly HFlowContainer buttons = new();
    private int[] turns;
    private bool busy;
    private bool stopping;
    private bool collapsed;
    private bool captureRequested;
    private CancellationTokenSource? album;
    private string previousStatus = "";
    private bool summaryOnly;
    private ulong advanceAfter;
    private static readonly double[] Speeds = [0.5, 1, 2, 4];

    internal ReplayToolbar(SceneTree tree, RunInfo run, RoomInfo room, bool continuous)
    {
        this.tree = tree;
        this.run = run;
        this.room = room;
        turns = ReplayStore.LoadEvents(run.Id, room.Index).Where(e => e.Turn > 0).Select(e => e.Turn).Distinct().Order().ToArray();
        layer.Name = "ReplayTheSpireControls";
        var inputShield = new ColorRect { Color = Colors.Transparent, MouseFilter = Control.MouseFilterEnum.Stop };
        layer.AddChild(inputShield);
        inputShield.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        inputShield.GuiInput += input => { if (input is InputEventMouseButton) inputShield.AcceptEvent(); };
        layer.AddChild(summary);
        summary.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        summary.OffsetLeft = -240; summary.OffsetRight = 240;
        summary.OffsetTop = -110; summary.OffsetBottom = 110;
        summary.AddChild(summaryBody);
        summary.Visible = false;
        layer.AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
        panel.OffsetTop = 64;
        panel.OffsetBottom = 104;
        panel.AddChild(sheet);
        var head = Row();
        sheet.AddChild(head);
        position.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(position);
        collapse = Action(T("收起", "Hide"), Toggle);
        collapse.Name = "CollapseReplay";
        exit = Action(T("返回回放库", "Library"), Leave);
        exit.Name = "BackToLibrary";
        head.AddChild(collapse);
        head.AddChild(exit);
        cancelAlbum = Action(T("停止导出", "Cancel export"), () => album?.Cancel());
        cancelAlbum.Name = "CancelAlbum";
        cancelAlbum.Visible = false;
        head.AddChild(cancelAlbum);
        var route = Row();
        sheet.AddChild(route);
        StyleButton(floors);
        floors.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        floors.FitToLongestItem = false;
        foreach (var r in run.Rooms) floors.AddItem($"{ReplayBrowser.FloorLabel(r)}    {ReplayBrowser.RoomName(r.Name)}");
        floors.Select(run.Rooms.FindIndex(r => r.Index == room.Index));
        floors.ItemSelected += i => Go(run.Rooms[(int)i]);
        route.AddChild(floors);
        continuousPlay.Text = T("连续播放", "Autoplay floors");
        Typeface(continuousPlay, 14);
        continuousPlay.ButtonPressed = continuous;
        continuousPlay.Toggled += _ => advanceAfter = 0;
        route.AddChild(continuousPlay);
        var timing = new CheckButton { Text = T("保留思考时间", "Keep decision times"), ButtonPressed = ReplayPlayer.OriginalTiming };
        Typeface(timing, 14);
        timing.Toggled += value => ReplayPlayer.OriginalTiming = value;
        route.AddChild(timing);
        sheet.AddChild(buttons);
        previous = Action(T("上一回合", "Prev turn"), () => Seek(-1));
        pause = Action(T("暂停", "Pause"), () => ReplayPlayer.SetPaused(!ReplayPlayer.IsPaused), true);
        step = Action(T("下一手", "Step"), () => ReplayPlayer.Step());
        next = Action(T("下一回合", "Next turn"), () => Seek(1));
        pause.Name = "PauseReplay";
        step.Name = "StepReplay";
        previous.Name = "PreviousTurn";
        next.Name = "NextTurn";
        pause.TooltipText = T("空格键暂停或继续", "Press Space to pause or resume");
        foreach (var item in new[] { previous, pause, step, next }) buttons.AddChild(item);
        StyleButton(speed);
        foreach (double s in Speeds) speed.AddItem($"{s:0.#}×");
        speed.Select(1);
        speed.ItemSelected += index => ReplayPlayer.SetSpeed(Speeds[(int)index]);
        buttons.AddChild(speed);
        snapshot = Action(T("截取此刻", "Save frame"), SaveFrame);
        buttons.AddChild(snapshot);
        sheet.AddChild(note);
        note.Visible = false;
    }

    internal void Show()
    {
        tree.Root.AddChild(layer);
        tree.Root.SizeChanged += Resize;
        tree.Root.WindowInput += OnWindowInput;
        layer.TreeExiting += () =>
        {
            tree.Root.SizeChanged -= Resize;
            tree.Root.WindowInput -= OnWindowInput;
        };
        var timer = new Godot.Timer
        {
            WaitTime = 0.22, ProcessMode = Node.ProcessModeEnum.Always, IgnoreTimeScale = true
        };
        layer.AddChild(timer);
        timer.Timeout += Refresh;
        timer.Start();
        Resize();
        Refresh();
    }

    internal async Task OpenRoomAsync(RoomInfo target, int turn, int? decision = null)
    {
        busy = true;
        try
        {
            var data = ReplayStore.LoadRoom(run.Id, target.Index);
            room = target;
            floors.Select(run.Rooms.FindIndex(r => r.Index == room.Index));
            turns = data.Events.Where(e => e.Turn > 0).Select(e => e.Turn).Distinct().Order().ToArray();
            summaryOnly = false;
            advanceAfter = 0;
            summary.Visible = false;
            previousStatus = "";
            if (data.Combat is { Length: > 0 } combat)
            {
                if (decision is > 0) await ReplayPlayer.PlayDecisionAsync(combat, decision.Value, data.Events);
                else await ReplayPlayer.PlayAsync(combat, turn, data.Events);
            }
            else if (data.Checkpoint is { Length: > 0 } checkpoint)
                await ReplayPlayer.PlayRoomAsync(checkpoint, data.Events, decision ?? 0);
            else
            {
                if (ReplayPlayer.IsActive) await ReplayPlayer.StopAsync();
                summaryOnly = true;
                Clear(summaryBody);
                summaryBody.AddChild(Text($"{ReplayBrowser.FloorLabel(room)}  ·  {room.Name}", 21));
                var list = new ItemList { CustomMinimumSize = new Vector2(340, 160) };
                Typeface(list);
                summaryBody.AddChild(list);
                foreach (var e in data.Events)
                    list.AddItem(e.DecisionSeconds.HasValue ? $"{e.Label}    {e.DecisionSeconds:F1} s" : e.Label);
                if (list.ItemCount == 0) list.AddItem(T("本层没有决策记录。", "No recorded choices on this floor."));
                summary.Visible = true;
                Feedback(T("楼层决策记录", "Recorded floor decisions"));
            }
        }
        finally { busy = false; }
    }

    private async void Go(RoomInfo destination)
    {
        if (busy || stopping || album != null || captureRequested) return;
        try { await OpenRoomAsync(destination, 0); }
        catch (Exception e)
        {
            continuousPlay.ButtonPressed = false;
            Feedback(e.GetBaseException().Message);
        }
    }

    private void OnWindowInput(InputEvent input)
    {
        if (input is not (InputEventKey or InputEventJoypadButton or InputEventJoypadMotion)) return;
        if (floors.GetPopup().Visible || speed.GetPopup().Visible) return;
        tree.Root.SetInputAsHandled();
        var key = input switch
        {
            InputEventKey { Pressed: true, Echo: false } k => k.Keycode,
            InputEventJoypadButton { Pressed: true } j => j.ButtonIndex switch
            {
                JoyButton.A => Key.Enter, JoyButton.B => Key.Escape,
                JoyButton.DpadLeft or JoyButton.DpadUp => Key.Left,
                JoyButton.DpadRight or JoyButton.DpadDown => Key.Right, _ => Key.None
            },
            _ => Key.None
        };
        if (key == Key.Escape)
        {
            if (album != null) album.Cancel();
            else Leave();
            return;
        }
        if (busy || album != null || stopping) return;
        if (key == Key.Space && ReplayPlayer.IsReady)
        {
            ReplayPlayer.SetPaused(!ReplayPlayer.IsPaused);
            return;
        }
        if (key is not (Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down or Key.Enter or Key.KpEnter)) return;
        var controls = panel.FindChildren("*", "BaseButton", true, false).OfType<BaseButton>()
            .Where(b => b.IsVisibleInTree() && !b.Disabled).ToArray();
        if (controls.Length == 0) return;
        int current = Array.IndexOf(controls, tree.Root.GuiGetFocusOwner() as BaseButton);
        if (current >= 0 && key is Key.Enter or Key.KpEnter)
        {
            var control = controls[current];
            if (control is OptionButton selector) selector.ShowPopup();
            else if (control.ToggleMode) control.ButtonPressed = !control.ButtonPressed;
            else control.EmitSignal(BaseButton.SignalName.Pressed);
            return;
        }
        bool back = key is Key.Left or Key.Up || key == Key.Tab && input is InputEventKey { ShiftPressed: true };
        int index = current < 0 ? 0 : (current + controls.Length + (back ? -1 : 1)) % controls.Length;
        controls[index].GrabFocus();
    }

    internal void Remove()
    {
        if (!GodotObject.IsInstanceValid(layer) || layer.IsQueuedForDeletion()) return;
        layer.Hide();
        layer.QueueFree();
    }

    private void Refresh()
    {
        int turn = ReplayPlayer.CurrentTurn;
        position.Text = room.HasCombat ? $"{T("回合", "Turn")} {turn}    ·    {ReplayPlayer.CurrentEvent}/{ReplayPlayer.EventCount}" :
            summaryOnly ? T("决策记录", "Decisions") : $"{T("选择", "Choice")} {ReplayPlayer.CurrentEvent}/{ReplayPlayer.EventCount}";
        bool canPlay = ReplayPlayer.IsActive && ReplayPlayer.IsReady && !busy && !stopping && album == null;
        bool paused = ReplayPlayer.IsPaused;
        pause.Text = paused ? T("继续", "Resume") : T("暂停", "Pause");
        pause.Disabled = !canPlay || ReplayPlayer.IsFinished;
        step.Disabled = !canPlay || !ReplayPlayer.CanStep;
        previous.Visible = next.Visible = room.HasCombat;
        previous.Disabled = !canPlay || !turns.Any(t => t < turn);
        next.Disabled = !canPlay || !turns.Any(t => t > turn);
        snapshot.Disabled = !canPlay || captureRequested;
        speed.Disabled = !canPlay;
        var currentStatus = ReplayPlayer.Status;
        if (previousStatus != currentStatus && album == null && !summaryOnly)
        {
            previousStatus = currentStatus;
            Feedback(currentStatus);
        }
        floors.Disabled = continuousPlay.Disabled = busy || album != null || stopping || captureRequested;
        bool completed = summaryOnly || ReplayPlayer.IsFinished && !ReplayPlayer.HasError;
        if (completed && !floors.Disabled && continuousPlay.ButtonPressed)
        {
            if (advanceAfter == 0) advanceAfter = Time.GetTicksMsec() + (summaryOnly ? 4000UL : 1300UL);
            if (Time.GetTicksMsec() >= advanceAfter)
            {
                int i = run.Rooms.FindIndex(r => r.Index == room.Index) + 1;
                if (i > 0 && i < run.Rooms.Count) Go(run.Rooms[i]);
                else
                {
                    continuousPlay.ButtonPressed = false;
                    Feedback(T("整局已播完。", "End of run."));
                }
            }
        }
    }

    private void Toggle()
    {
        collapsed = !collapsed;
        buttons.Visible = !collapsed;
        floors.GetParent<Control>().Visible = !collapsed;
        note.Visible = !collapsed && note.Text.Length > 0;
        collapse.Text = collapsed ? T("展开", "Show") : T("收起", "Hide");
        Resize();
    }

    private void Resize()
    {
        float factor = ReadabilityScale(tree.Root);
        float width = Math.Max(260, Math.Min(collapsed ? 390 : 660, tree.Root.GetVisibleRect().Size.X / factor - 24));
        panel.Scale = Vector2.One * factor;
        panel.PivotOffset = new Vector2(width / 2, 0);
        panel.OffsetLeft = -width / 2;
        panel.OffsetRight = width / 2;
        panel.ResetSize();
        // Keep the anchor width after resetting any extra height from a wrapped toolbar.
        panel.OffsetLeft = -width / 2;
        panel.OffsetRight = width / 2;
        summary.Scale = Vector2.One * factor;
        summary.PivotOffset = summary.Size / 2;
    }

    private async void Seek(int direction)
    {
        if (busy || stopping) return;
        int current = ReplayPlayer.CurrentTurn;
        int target = direction < 0 ? turns.LastOrDefault(t => t < current) : turns.FirstOrDefault(t => t > current);
        if (target <= 0) return;
        busy = true;
        Refresh();
        try { await ReplayPlayer.SeekTurnAsync(target); }
        catch (Exception e) { Feedback(e.GetBaseException().Message); }
        finally { busy = false; }
    }

    private async void Leave()
    {
        if (stopping || busy || captureRequested) return;
        stopping = true;
        exit.Disabled = true;
        try
        {
            await ReplayPlayer.StopAsync();
            Remove();
            ReplayUi.Open(tree, run.Id, room.Index);
        }
        catch (Exception e)
        {
            stopping = false;
            exit.Disabled = false;
            Feedback(e.GetBaseException().Message);
        }
    }

    private async void SaveFrame()
    {
        if (captureRequested || stopping || busy || album != null) return;
        captureRequested = true;
        bool wasPaused = ReplayPlayer.IsPaused;
        int turn = ReplayPlayer.CurrentTurn;
        int action = ReplayPlayer.CurrentEvent;
        try
        {
            ReplayPlayer.SetPaused(true);
            await WriteFrame($"t{turn:D3}-e{action:D5}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.png", CancellationToken.None);
            Feedback(T("已保存原生画面，可在本层图集中查看。", "Frame saved to this floor's gallery."));
        }
        catch (Exception e) { Feedback(e.GetBaseException().Message); }
        finally
        {
            if (GodotObject.IsInstanceValid(layer))
            {
                if (!wasPaused && ReplayPlayer.IsReady && !ReplayPlayer.IsFinished) ReplayPlayer.SetPaused(false);
            }
            captureRequested = false;
        }
    }

    internal async Task CaptureAlbumAsync()
    {
        await WithAlbum(async token => await CaptureRoomFrames(token, 4, null, ""));
    }

    internal async Task<string> CaptureForumAsync()
    {
        string exports = ReplayStore.ExportsDirectory;
        Directory.CreateDirectory(exports);
        string unique = Guid.NewGuid().ToString("N");
        string pending = Path.Combine(exports, ".album-" + unique);
        string complete = Path.Combine(exports, $"Replay-{DateTime.Now:yyyyMMdd-HHmmss}-{unique[..8]}");
        Directory.CreateDirectory(pending);
        try
        {
            await WithAlbum(async token =>
            {
                for (int i = 0; i < run.Rooms.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var nextRoom = run.Rooms[i];
                    string count = $"{i + 1}/{run.Rooms.Count}  ·  {ReplayBrowser.FloorLabel(nextRoom)}   ";
                    Feedback(count + T("正在整理图集…", "Collecting frames…"));
                    if (!nextRoom.HasCombat && !nextRoom.HasCheckpoint) continue;
                    string dir = Path.Combine(pending, $"room-{nextRoom.Index:D4}");
                    Directory.CreateDirectory(dir);
                    try
                    {
                        await OpenRoomAsync(nextRoom, nextRoom.HasCombat ? 1 : 0, nextRoom.HasCombat ? null : 1);
                        await CaptureRoomFrames(token, nextRoom.HasCombat ? 2 : 1, dir, count);
                    }
                    catch (InvalidDataException) when (!nextRoom.HasCombat)
                    {
                        // A room's recorded choices still produce a post when that native
                        // checkpoint cannot be reconstructed in the installed environment.
                        if (ReplayPlayer.IsActive) await ReplayPlayer.StopAsync();
                    }
                }
                await Task.Run(() => Gallery.WriteForum(run.Id, pending, token), token);
                token.ThrowIfCancellationRequested();
                Directory.Move(pending, complete);
            });
            return Path.Combine(complete, "index.html");
        }
        finally
        {
            if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
        }
    }

    private async Task WithAlbum(Func<CancellationToken, Task> capture)
    {
        using var cancellation = new CancellationTokenSource();
        album = cancellation;
        double savedSpeed = ReplayPlayer.Speed;
        bool originalTiming = ReplayPlayer.OriginalTiming;
        cancelAlbum.Visible = true;
        exit.Disabled = collapse.Disabled = true;
        try
        {
            ReplayPlayer.OriginalTiming = false;
            ReplayPlayer.SetSpeed(4);
            await capture(cancellation.Token);
        }
        finally
        {
            album = null;
            ReplayPlayer.OriginalTiming = originalTiming;
            ReplayPlayer.SetSpeed(savedSpeed);
            cancelAlbum.Visible = false;
            exit.Disabled = collapse.Disabled = false;
        }
    }

    private async Task CaptureRoomFrames(CancellationToken token, int slices, string? destination, string prefix)
    {
        var timeline = destination != null && room.HasCombat ? ReplayStore.LoadEvents(run.Id, room.Index) : [];
        var moment = Gallery.FeaturedCard(timeline);
        int targetDecision = moment == null ? 0 : moment.Decision + new BattleTimeline(timeline).Before.Length;
        bool featured = false;
        int captured = 0;
        int lastEvent = -1;
        int nextSlice = 1;
        while (ReplayPlayer.IsActive)
        {
            await WaitAtDecision(token);
            int at = ReplayPlayer.CurrentEvent;
            bool ended = ReplayPlayer.IsFinished;
            if (ended && (ReplayPlayer.HasError || room.HasCombat && !ReplayPlayer.IsVerified))
                throw new InvalidDataException(ReplayPlayer.Status);
            bool atCard = moment != null && !featured && ReplayPlayer.CurrentDecision >= targetDecision;
            bool atSlice = moment == null && nextSlice < slices && (long)at * slices >= (long)nextSlice * ReplayPlayer.EventCount;
            if (at != lastEvent && (captured == 0 || ended || atCard || atSlice))
            {
                if (captured > 0)
                    while (nextSlice < slices && (long)at * slices >= (long)nextSlice * ReplayPlayer.EventCount) nextSlice++;
                string name = room.HasCombat ? $"auto-{at:D6}-turn-{ReplayPlayer.CurrentTurn:D3}" : $"choice-{at:D5}";
                if (atCard)
                {
                    name += $"-decision-{moment!.Decision:D5}";
                    featured = true;
                }
                await WriteFrame(name + (destination == null ? ".png" : ".jpg"), token, destination, featuredCard: atCard);
                captured++;
                lastEvent = at;
            }
            Feedback(prefix + T($"已截取 {captured} 张 · 动作 {at}/{ReplayPlayer.EventCount}", $"{captured} frames · action {at}/{ReplayPlayer.EventCount}"));
            if (ended) return;
            ReplayPlayer.Step();
        }
        throw new InvalidOperationException(T("回放已关闭。", "Replay closed."));
    }

    private async Task WaitAtDecision(CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (ReplayPlayer.IsActive && !ReplayPlayer.IsPaused && !ReplayPlayer.IsFinished)
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed.TotalSeconds > 90)
                throw new TimeoutException(T("等待回放定位超时。", "Timed out waiting for the replay."));
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        token.ThrowIfCancellationRequested();
        if (!ReplayPlayer.IsActive) throw new InvalidOperationException(T("回放已关闭。", "Replay closed."));
    }

    private async Task WriteFrame(string filename, CancellationToken token, string? destination = null, bool featuredCard = false)
    {
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(T("请在图形窗口中截取画面。", "Open the graphical game to capture a frame."));
        // Decision stepping can freeze a turn banner over the card and damage it illustrates.
        Control[] banners = featuredCard && NCombatRoom.Instance is { } combat
            ? combat.GetChildren().OfType<Control>().Where(node => node.Visible &&
                node is NPlayerTurnBanner or NEnemyTurnBanner or NCombatStartBanner).ToArray()
            : [];
        panel.Hide();
        try
        {
            foreach (var banner in banners) banner.Hide();
            // Read only after a complete frame has actually been presented with the toolbar hidden.
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            bool drawn = false;
            void Drawn() => drawn = true;
            RenderingServer.FramePostDraw += Drawn;
            try
            {
                var timeout = Stopwatch.StartNew();
                while (!drawn)
                {
                    token.ThrowIfCancellationRequested();
                    if (timeout.Elapsed.TotalSeconds > 10)
                        throw new TimeoutException(T("请恢复游戏窗口后重试截图。", "Restore the game window and try capturing again."));
                    await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                }
            }
            finally { RenderingServer.FramePostDraw -= Drawn; }
            using var image = await ReadPresentedFrame(token, destination != null);
            if (image == null || image.IsEmpty()) throw new IOException(T("无法读取游戏画面。", "Could not read the game frame."));
            string path = Path.Combine(destination ?? Gallery.FrameDirectory(run.Id, room.Index), filename);
            string pending = path + ".partial";
            try
            {
                var error = destination == null ? image.SavePng(pending) : image.SaveJpg(pending, 0.91f);
                if (error != Godot.Error.Ok) throw new IOException($"{T("截图保存失败", "Could not save frame")} ({error})");
                File.Move(pending, path, true);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
        finally
        {
            foreach (var banner in banners)
                if (GodotObject.IsInstanceValid(banner) && !banner.IsQueuedForDeletion()) banner.Show();
            panel.Show();
        }
    }

    private async Task<Image> ReadPresentedFrame(CancellationToken token, bool albumFrame)
    {
        // At a decision boundary the playback clock can freeze a map fade halfway through.
        // Advance visual tweens while the decision loop remains paused, then restore its clock.
        double scale = Engine.TimeScale;
        bool paused = tree.Paused;
        var clock = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var image = tree.Root.GetTexture().GetImage();
                if (!albumFrame || image == null || image.IsEmpty()) return image!;
                float min = 1, max = 0;
                for (int y = 1; y < 8; y++)
                    for (int x = 1; x < 12; x++)
                    {
                        float light = image.GetPixel(x * image.GetWidth() / 12, y * image.GetHeight() / 8).Luminance;
                        min = Math.Min(min, light);
                        max = Math.Max(max, light);
                    }
                if (max - min >= .09f) return image;
                if (clock.Elapsed.TotalSeconds >= 2)
                {
                    image.Dispose();
                    throw new InvalidDataException(T("此处仍在切换场景。", "The scene is still transitioning."));
                }
                image.Dispose();
                if (paused) tree.Paused = false;
                if (scale == 0) Engine.TimeScale = 1;
                await tree.ToSignal(tree.CreateTimer(.1, true, false, true), SceneTreeTimer.SignalName.Timeout);
            }
        }
        finally
        {
            tree.Paused = paused;
            Engine.TimeScale = scale;
        }
    }

    private void Feedback(string text)
    {
        note.Text = text;
        note.TooltipText = text;
        note.Visible = !collapsed && text.Length > 0;
    }
}
