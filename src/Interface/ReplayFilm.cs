using System.Diagnostics;
using Godot;
using SpireReplay.Media;
using SpireReplay.Playback;
using static SpireReplay.Interface.ReplayStyle;

namespace SpireReplay.Interface;

internal sealed class ReplayFilm
{
    private readonly SceneTree tree;
    private readonly RunInfo run;
    private readonly IReadOnlyList<RoomInfo> rooms;
    private readonly CancellationTokenSource cancel = new();
    private readonly Stopwatch elapsed = new();
    private readonly Window progress = new()
    {
        Name = "ReplayFilmProgress", Visible = false, ForceNative = true, Transient = true,
        Exclusive = false, Unfocusable = true, Unresizable = true,
        ProcessMode = Node.ProcessModeEnum.Always, Size = new Vector2I(380, 160),
    };
    private readonly Label place = Text("", 16);
    private readonly Label time = Text("", 14, true);
    private readonly ProgressBar bar = new() { ShowPercentage = false, CustomMinimumSize = new Vector2(0, 8) };
    private readonly Button stop;
    private readonly CanvasLayer film = new() { Layer = 100, ProcessMode = Node.ProcessModeEnum.Always };
    private readonly ColorRect input = new() { Color = Colors.Transparent, MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly ColorRect card = new() { Color = Ink, MouseFilter = Control.MouseFilterEnum.Stop };
    private readonly VBoxContainer cardBody = Column(14);
    private int roomNumber;
    private int cardPage;
    private int cardPages;
    private bool finalizing;

    internal ReplayFilm(SceneTree tree, RunInfo run, IReadOnlyList<RoomInfo> rooms)
    {
        this.tree = tree;
        this.run = run;
        this.rooms = rooms;
        progress.Title = T("回放尖塔 · 导出视频", "Replay the Spire · Export video");
        var panel = Panel(Ink, 12);
        progress.AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var body = Column(6);
        panel.AddChild(body);
        body.AddChild(place);
        body.AddChild(bar);
        body.AddChild(time);
        stop = Action(T("取消导出", "Cancel export"), Cancel);
        stop.Name = "CancelVideoExport";
        body.AddChild(stop);
        progress.CloseRequested += Cancel;
        film.AddChild(input);
        input.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        film.AddChild(card);
        card.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var area = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        card.AddChild(area);
        area.SetAnchorAndOffset(Side.Left, 0.14f, 0);
        area.SetAnchorAndOffset(Side.Right, 0.86f, 0);
        area.SetAnchorAndOffset(Side.Top, 0.15f, 0);
        area.SetAnchorAndOffset(Side.Bottom, 0.85f, 0);
        area.AddChild(cardBody);
        card.Hide();
    }

    internal async Task<string> WriteAsync(bool wholeRun, int targetHeight, float? audioGain)
    {
        if (rooms.Count == 0) throw new InvalidOperationException(T("本局没有楼层可供导出。", "This run has no floors to export."));
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(T("请在游戏窗口中导出视频。", "Open the graphical game to export video."));
        string? mismatch = ReplayStore.CheckCompatibility(run);
        if (mismatch != null) throw new InvalidOperationException(mismatch);
        string directory = Path.Combine(ReplayStore.ExportsDirectory, "video");
        Directory.CreateDirectory(directory);
        string scope = wholeRun ? "run" : $"floor-{rooms[0].Floor:D2}-room-{rooms[0].Index:D2}";
        string path = Path.Combine(directory, $"{scope}-{targetHeight}p-{run.Id[..8]}-{DateTime.Now:yyyyMMdd-HHmmssfff}.mp4");
        double previousSpeed = ReplayPlayer.Speed;
        tree.Root.AddChild(film);
        tree.Root.AddChild(progress);
        tree.Root.WindowInput += InterceptInput;
        var timer = new Godot.Timer { WaitTime = 0.2, IgnoreTimeScale = true, ProcessMode = Node.ProcessModeEnum.Always };
        progress.AddChild(timer);
        timer.Timeout += UpdateStatus;
        try
        {
            elapsed.Start();
            UpdateStatus();
            progress.Position = tree.Root.Position + new Vector2I(Math.Max(0, tree.Root.Size.X - 400), 26);
            progress.Show();
            // A display backend may lack independent windows. An embedded progress HUD
            // would be composited into every frame; Escape remains available in that case.
            if (progress.IsEmbedded()) progress.Hide();
            timer.Start();
            ReplayPlayer.SetSpeed(1);
            // The recorder's first readback should see the run, not the library
            // subwindow which the player dismissed in the launch frame.
            cardBody.AddChild(Text(run.Character, 48));
            cardBody.AddChild(Text(wholeRun ? T("整局回放", "Full run") : $"{ReplayBrowser.FloorLabel(rooms[0])}    {rooms[0].Name}", 30));
            cardBody.AddChild(Text($"{T("种子", "Seed")}  {run.Seed}", 21, true));
            card.Show();
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            cancel.Token.ThrowIfCancellationRequested();
            return await VideoExporter.RecordAsync(path, PlayRooms, cancel.Token, targetHeight, audioGain);
        }
        finally
        {
            tree.Root.WindowInput -= InterceptInput;
            try
            {
                if (ReplayPlayer.IsActive) await ReplayPlayer.StopAsync();
            }
            finally
            {
                ReplayPlayer.SetSpeed(previousSpeed);
                progress.Hide();
                progress.QueueFree();
                film.Hide();
                film.QueueFree();
                cancel.Dispose();
            }
        }
    }

    private async Task PlayRooms(CancellationToken token)
    {
        for (roomNumber = 0; roomNumber < rooms.Count; roomNumber++)
        {
            token.ThrowIfCancellationRequested();
            var room = rooms[roomNumber];
            var data = ReplayStore.LoadRoom(run.Id, room.Index);
            cardPage = cardPages = 0;
            if (data.Combat is { Length: > 0 } combat)
            {
                await ReplayPlayer.PlayAsync(combat, 0, data.Events);
                card.Hide();
            }
            else if (data.Checkpoint is { Length: > 0 } checkpoint)
            {
                await ReplayPlayer.PlayRoomAsync(checkpoint, data.Events);
                card.Hide();
            }
            else
            {
                if (ReplayPlayer.IsActive) await ReplayPlayer.StopAsync();
                await ShowDecisions(room, data.Events, token);
                continue;
            }
            while (ReplayPlayer.IsActive && !ReplayPlayer.IsFinished)
            {
                token.ThrowIfCancellationRequested();
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            token.ThrowIfCancellationRequested();
            if (!ReplayPlayer.IsActive || ReplayPlayer.HasError)
                throw new InvalidOperationException($"{ReplayBrowser.FloorLabel(room)} · {ReplayPlayer.Status}");
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        finalizing = true;
        UpdateStatus();
    }

    private async Task ShowDecisions(RoomInfo room, List<ReplayEvent> events, CancellationToken token)
    {
        const int pageSize = 5;
        cardPages = Math.Max(1, (events.Count + pageSize - 1) / pageSize);
        for (cardPage = 0; cardPage < cardPages; cardPage++)
        {
            Clear(cardBody);
            cardBody.AddChild(Text($"{run.Character}    ·    {ReplayBrowser.FloorLabel(room)}", 24, true));
            cardBody.AddChild(Text(room.Name, 40));
            foreach (var choice in events.Skip(cardPage * pageSize).Take(pageSize))
            {
                string caption = choice.DecisionSeconds is double seconds ? $"{choice.Label}    {seconds:0.#} s" : choice.Label;
                var line = Text(caption, 23);
                line.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                cardBody.AddChild(line);
            }
            card.Show();
            var until = Time.GetTicksMsec() + (events.Count == 0 ? 900UL : 2600UL);
            while (Time.GetTicksMsec() < until)
            {
                token.ThrowIfCancellationRequested();
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
        }
    }

    private void UpdateStatus()
    {
        int i = Math.Min(roomNumber, rooms.Count - 1);
        var room = rooms[i];
        place.Text = finalizing ? T("正在写入 MP4…", "Writing MP4…") :
            $"{ReplayBrowser.FloorLabel(room)}  ·  {room.Name}    ({i + 1}/{rooms.Count})";
        double fraction = cardPages > 0 ? (double)cardPage / cardPages :
            ReplayPlayer.EventCount > 0 ? (double)ReplayPlayer.CurrentEvent / ReplayPlayer.EventCount : 0;
        bar.Value = finalizing ? 100 : (i + Math.Clamp(fraction, 0, 1)) * 100 / rooms.Count;
        time.Text = $"{T("已用时间", "Elapsed")}  {elapsed.Elapsed:hh\\:mm\\:ss}";
    }

    private void InterceptInput(InputEvent e)
    {
        // Native room controls must never accept real player choices while recording.
        // Keep the export window's cancel button and the game's Escape shortcut usable.
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) Cancel();
        if (e is InputEventKey or InputEventJoypadButton or InputEventJoypadMotion or InputEventMouseButton)
            tree.Root.SetInputAsHandled();
    }

    private void Cancel()
    {
        if (cancel.IsCancellationRequested) return;
        stop.Disabled = true;
        stop.Text = T("正在取消…", "Cancelling…");
        cancel.Cancel();
    }
}
