using Godot;
using MegaCrit.Sts2.Core.Saves;
using SpireReplay.Media;
using SpireReplay.Playback;
using static SpireReplay.Interface.ReplayStyle;

namespace SpireReplay.Interface;

internal sealed class ReplayBrowser
{
    private static string? lastRunId;
    private static int lastRoomIndex;
    private readonly SceneTree tree;
    internal Window Window { get; } = new() { Name = "ReplayTheSpireLibrary", Title = "Replay the Spire", Visible = false, Borderless = true, Transient = true, Exclusive = true, ProcessMode = Node.ProcessModeEnum.Always };
    private readonly PanelContainer backdrop = Panel(Ink, 18);
    private readonly GridContainer selectors = new() { Columns = 2 };
    private readonly VBoxContainer page = Column(10);
    private readonly VBoxContainer detail = Column(8);
    private readonly OptionButton runs = new() { Name = "RunPicker" };
    private readonly OptionButton rooms = new() { Name = "RoomPicker" };
    private readonly Label metadata = Text("", 14, true);
    private readonly Label compatibility = Text("", 14);
    private readonly Label status = Text("", 14, true);
    private readonly Button openFile;
    private readonly Button openRun;
    private readonly Button copyPath;
    private readonly Button retryCatalog;
    private IReadOnlyList<RunInfo> catalog = [];
    private List<ReplayEvent> events = [];
    private RunInfo? run;
    private RoomInfo? room;
    private string? exportedPath;
    private bool busy;
    private bool writingFile;
    private int viewRevision;
    private int catalogRevision;
    private int selectionRevision;
    private int importRevision;
    private Action? retryRead;
    private readonly string? initialRun;
    private readonly int? initialRoom;

    internal ReplayBrowser(SceneTree tree, string? initialRun, int? initialRoom)
    {
        this.tree = tree;
        this.initialRun = initialRun;
        this.initialRoom = initialRoom;
        Window.Title = T("回放尖塔", "Replay the Spire");
        Window.CloseRequested += Close;
        Window.WindowInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } && !busy)
            {
                Window.SetInputAsHandled();
                Close();
            }
        };

        Window.AddChild(backdrop);
        backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        backdrop.AddChild(page);
        var header = Row();
        page.AddChild(header);
        var title = Text(T("回放尖塔", "Replay the Spire"), 25);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(title);
        header.AddChild(Action(T("导入回放", "Import"), PickFile));
        header.AddChild(Action(T("关闭", "Close"), Close));

        selectors.AddThemeConstantOverride("h_separation", 12);
        page.AddChild(selectors);
        var runField = Column(2);
        var roomField = Column(2);
        runField.SizeFlagsHorizontal = roomField.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        runField.SizeFlagsStretchRatio = 1.45f;
        selectors.AddChild(runField);
        selectors.AddChild(roomField);
        runField.AddChild(Text(T("对局", "Run"), 14, true));
        runField.AddChild(runs);
        StyleButton(runs);
        runs.FitToLongestItem = false;
        runs.ItemSelected += i => { selectionRevision++; SelectRun((int)i); };
        var info = Row();
        metadata.Name = "RunMetadata";
        metadata.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        metadata.MouseFilter = Control.MouseFilterEnum.Pass;
        page.AddChild(info);
        info.AddChild(metadata);
        openRun = Action(T("记录目录", "Run folder"), () => { if (run != null) RevealDirectory(ReplayStore.RunPath(run.Id)); });
        openRun.Name = "OpenRunFolder";
        openRun.TooltipText = T("打开这局回放的本地记录", "Open this run’s local replay files");
        openRun.Visible = false;
        info.AddChild(openRun);
        compatibility.Name = "ReplayCompatibility";
        compatibility.MouseFilter = Control.MouseFilterEnum.Pass;
        compatibility.AddThemeColorOverride("font_color", Gold);
        compatibility.Hide();
        page.AddChild(compatibility);
        roomField.AddChild(Text(T("楼层", "Floor"), 14, true));
        roomField.AddChild(rooms);
        StyleButton(rooms);
        rooms.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        rooms.FitToLongestItem = false;
        rooms.ItemSelected += i => { selectionRevision++; SelectRoom((int)i); };
        var body = Panel(Slate);
        Expand(body);
        page.AddChild(body);
        body.AddChild(detail);
        var bottom = Row();
        page.AddChild(bottom);
        status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        status.Name = "ExportStatus";
        bottom.AddChild(status);
        retryCatalog = Action(T("重试读取", "Retry loading"), () => retryRead?.Invoke());
        retryCatalog.Hide();
        bottom.AddChild(retryCatalog);
        copyPath = Action(T("复制路径", "Copy path"), () => { if (exportedPath != null) DisplayServer.ClipboardSet(exportedPath); });
        copyPath.Visible = false;
        bottom.AddChild(copyPath);
        openFile = Action(T("打开目录", "Open folder"), Reveal);
        openFile.Name = "OpenExportFolder";
        openFile.Visible = false;
        bottom.AddChild(openFile);
    }

    internal void Show()
    {
        tree.Root.AddChild(Window);
        var size = FitToDisplay();
        Window.Size = size;
        tree.Root.SizeChanged += Resize;
        Window.TreeExiting += () => tree.Root.SizeChanged -= Resize;
        Window.PopupCentered(size);
        _ = ReloadAsync(initialRun, initialRoom, selectionRevision);
    }

    private Vector2I FitToDisplay()
    {
        var parent = tree.Root;
        float factor = Window.IsEmbedded() ? ReadabilityScale(parent) : 1;
        var baseSize = new Vector2I(Math.Clamp(parent.Size.X - 32, 280, 1030), Math.Clamp(parent.Size.Y - 68, 300, 760));
        // Embedded Windows inherit the game's canvas stretch. Give the actual
        // panel its design size and scale its children together with the panel.
        // A second content stretcher on the Window leaves empty space on HiDPI.
        selectors.Columns = baseSize.X < 650 ? 1 : 2;
        backdrop.Position = Vector2.Zero;
        backdrop.Size = baseSize;
        backdrop.Scale = Vector2.One * factor;
        return new Vector2I(Mathf.RoundToInt(baseSize.X * factor), Mathf.RoundToInt(baseSize.Y * factor));
    }

    private void Resize()
    {
        Window.Size = FitToDisplay();
        var space = Window.IsEmbedded() ? (Vector2I)tree.Root.GetVisibleRect().Size : tree.Root.Size;
        Window.Position = (space - Window.Size) / 2 + (Window.IsEmbedded() ? Vector2I.Zero : tree.Root.Position);
    }

    private void Close()
    {
        if (busy) return;
        Window.Hide();
        Window.QueueFree();
    }

    private async Task<bool> ReloadAsync(string? requestedId, int? requestedRoom, int requestedSelection)
    {
        int revision = ++catalogRevision;
        bool opening = catalog.Count == 0;
        var originalFocus = Window.GuiGetFocusOwner();
        retryCatalog.Hide();
        retryRead = null;
        if (opening)
        {
            runs.Disabled = rooms.Disabled = true;
            runs.Clear();
            runs.AddItem(T("正在读取回放…", "Loading replays…"));
            Clear(detail);
        }
        try
        {
            var loaded = await Task.Run(ReplayStore.ListRuns);
            if (!IsOpen() || revision != catalogRevision) return false;
            if (selectionRevision != requestedSelection && run != null)
            {
                requestedId = run.Id;
                requestedRoom = room?.Index;
            }
            catalog = loaded; // ReplayStore already returns most recent first.
            runs.Clear();
            foreach (var item in catalog)
                runs.AddItem($"{item.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}    {item.Character}    {ResultText(item.Result)}");
            runs.Disabled = catalog.Count == 0;
            if (catalog.Count == 0)
            {
                runs.AddItem(T("暂无回放", "No replays yet"));
                run = null;
                room = null;
                events = [];
                viewRevision++;
                metadata.Text = "";
                compatibility.Hide();
                openRun.Hide();
                rooms.Clear();
                rooms.Disabled = true;
                Clear(detail);
                var hint = Text(T("完成一段单人游玩后，在这里回顾对局。也可以导入朋友的回放。", "Play a single-player run or import a friend's replay to start your library."));
                hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                detail.AddChild(hint);
                return true;
            }
            int index = 0;
            if (requestedId != null)
                for (int i = 0; i < catalog.Count; i++)
                    if (catalog[i].Id == requestedId) { index = i; break; }
            runs.Select(index);
            SelectRun(index, requestedRoom);
            if (opening && Window.Visible && Window.GuiGetFocusOwner() == originalFocus) runs.GrabFocus();
            return true;
        }
        catch (Exception e)
        {
            if (!IsOpen() || revision != catalogRevision) return false;
            if (opening)
            {
                runs.Clear();
                runs.AddItem(T("读取回放失败", "Could not load replays"));
            }
            Error(e);
            retryRead = async () =>
            {
                if (await ReloadAsync(requestedId, requestedRoom, requestedSelection) && IsOpen())
                    Message(T("回放库已刷新", "Library refreshed"));
            };
            retryCatalog.Show();
            return false;
        }
    }

    private void SelectRun(int index, int? requestedRoom = null)
    {
        if (index < 0 || index >= catalog.Count) return;
        run = catalog[index];
        openRun.Show();
        metadata.Text = $"{T("种子", "Seed")}  {run.Seed}     ·     {T("游戏版本", "Build")}  {run.Build}";
        metadata.TooltipText = metadata.Text;
        var explanation = ReplayCompatibility.Explain(run, GameEnvironment.Build, GameEnvironment.Mods);
        compatibility.Visible = explanation.HasValue;
        compatibility.Text = explanation?.Summary ?? "";
        compatibility.TooltipText = explanation?.Details ?? "";
        RefreshStorage(run, metadata.Text);
        rooms.Clear();
        foreach (var item in run.Rooms) rooms.AddItem($"{FloorLabel(item)}   {RoomName(item.Name)}");
        rooms.Disabled = run.Rooms.Count == 0;
        if (rooms.Disabled)
        {
            room = null;
            events = [];
            Clear(detail);
            detail.AddChild(Text(T("还没有已记录的房间。", "No rooms recorded yet.")));
            return;
        }
        int? prefer = requestedRoom ?? (lastRunId == run.Id ? lastRoomIndex : null);
        int at = run.Rooms.FindIndex(x => x.Index == prefer);
        if (at < 0) at = run.Rooms.FindLastIndex(x => x.HasCombat);
        if (at < 0) at = run.Rooms.FindLastIndex(x => x.HasCheckpoint || x.Floor > 0);
        if (at < 0) at = 0;
        rooms.Select(at);
        SelectRoom(at);
    }

    private async void RefreshStorage(RunInfo selected, string description)
    {
        try
        {
            var directory = ReplayStore.RunPath(selected.Id);
            long bytes = await Task.Run(() => new DirectoryInfo(directory).EnumerateFiles()
                .Where(file => !file.Name.Contains(".tmp-", StringComparison.Ordinal)).Sum(file => file.Length));
            if (!IsOpen() || !ReferenceEquals(run, selected)) return;
            string size = bytes >= 1048576 ? $"{bytes / 1048576.0:0.#} MiB" :
                bytes >= 1024 ? $"{bytes / 1024.0:0.#} KiB" : $"{bytes} B";
            metadata.Text = $"{description}     ·     {T("回放大小", "Replay size")}  {size}";
            metadata.TooltipText = $"{metadata.Text}  ({bytes:N0} B)";
        }
        catch (IOException) { } // A recording may be updated while the library is open.
        catch (UnauthorizedAccessException) { }
    }

    private void SelectRoom(int index)
    {
        if (run == null || index < 0 || index >= run.Rooms.Count) return;
        room = run.Rooms[index];
        lastRunId = run.Id;
        lastRoomIndex = room.Index;
        try { events = ReplayStore.LoadEvents(run.Id, room.Index); }
        catch (Exception e) { events = []; Error(e); }
        Timeline();
    }

    private void Timeline()
    {
        viewRevision++;
        Clear(detail);
        if (room == null || run == null) return;
        var bar = new HFlowContainer();
        detail.AddChild(bar);
        var activeRun = run;
        var activeRoom = room;
        string? mismatch = compatibility.Visible ? compatibility.TooltipText : ReplayStore.CheckCompatibility(run);
        var tour = Action(T("播放整局", "Play run"), () => StartAt(activeRun.Rooms[0], 0, continuous: true), !room.HasCombat);
        tour.Name = "PlayRun";
        tour.Disabled = mismatch != null;
        tour.TooltipText = mismatch ?? "";
        var play = Action(room.HasCombat ? T("播放本层战斗", "Play floor combat") : T("观看本层", "Watch floor"), () => StartAt(activeRoom, 0), true);
        play.Name = "PlayRoom";
        bar.AddChild(play);
        play.Visible = room.HasCombat || room.HasCheckpoint;
        play.Disabled = !play.Visible || mismatch != null;
        play.TooltipText = mismatch ?? "";
        bar.AddChild(tour);
        var totals = Action(T("全局卡牌统计", "Card totals"), CardTotals);
        totals.Name = "CardTotals";
        bar.AddChild(totals);
        if (!play.Disabled && room.HasCombat)
        {
            var album = Action(T("生成实机图集", "Capture combat gallery"), () => StartAt(activeRoom, 1, captureAlbum: true));
            album.Name = "CaptureAlbum";
            bar.AddChild(album);
        }
        var forum = Action(T("生成整局直播贴", "Create run photo journal"), () => StartAt(activeRoom, 0, forumAlbum: true));
        forum.Name = "CaptureRunAlbum";
        forum.Disabled = mismatch != null;
        forum.TooltipText = mismatch ?? T("逐层截取游戏画面并生成图文帖", "Capture each floor and export a ready-to-share photo journal");
        bar.AddChild(forum);
        var video = Action(T("本层视频", "Floor video"), () => ExportFilm(activeRun, activeRoom));
        video.Name = "ExportRoomVideo";
        video.Disabled = mismatch != null;
        video.TooltipText = mismatch ?? T("导出本层有声 MP4", "Export this floor as an MP4 with game audio");
        bar.AddChild(video);
        var exports = new MenuButton { Text = T("导出 ▾", "Export ▾"), Name = "ExportOptions" };
        StyleButton(exports);
        var menu = exports.GetPopup();
        menu.AddItem(T("本层决策图集", "Floor decision gallery"), 0);
        menu.AddItem(T("整局图集索引", "Run gallery index"), 1);
        menu.AddItem(T("整局视频 · 720p", "Full run video · 720p"), 3);
        menu.SetItemDisabled(menu.GetItemIndex(3), mismatch != null);
        var highQuality = new PopupMenu { Name = "HighQualityVideo" };
        highQuality.AddItem(T("本层视频", "Floor video"), 0);
        highQuality.AddItem(T("整局视频", "Full run video"), 1);
        highQuality.IdPressed += id => ExportFilm(activeRun, id == 0 ? activeRoom : null, 1080);
        menu.AddSubmenuNodeItem(T("高清视频 · 1080p", "HD video · 1080p"), highQuality, 4);
        menu.SetItemDisabled(menu.GetItemIndex(4), mismatch != null);
        menu.AddSeparator();
        menu.AddItem(T("整局回放文件", "Replay file"), 2);
        menu.AddSeparator();
        menu.AddCheckItem(T("保留思考时间", "Keep decision times"), 5);
        menu.SetItemChecked(menu.GetItemIndex(5), ReplayPlayer.OriginalTiming);
        menu.AboutToPopup += () => menu.SetItemChecked(menu.GetItemIndex(5), ReplayPlayer.OriginalTiming);
        menu.IdPressed += id =>
        {
            if (id == 5)
            {
                ReplayPlayer.OriginalTiming = !ReplayPlayer.OriginalTiming;
                menu.SetItemChecked(menu.GetItemIndex(5), ReplayPlayer.OriginalTiming);
                return;
            }
            if (id == 3) { ExportFilm(activeRun, null); return; }
            Export(() => id switch
            {
                0 => Gallery.Export(activeRun.Id, activeRoom.Index),
                1 => Gallery.Export(activeRun.Id, -1),
                _ => ReplayStore.Export(activeRun.Id)
            });
        };
        bar.AddChild(exports);

        int numTurns = events.Select(e => e.Turn).DefaultIfEmpty(0).Max();
        string summary = T("本层时间线", "Floor timeline");
        if (numTurns > 0) summary += $"    ·    {T($"{numTurns} 回合", $"{numTurns} turns")}";
        var filter = new OptionButton { Name = "TimelineFilter" };
        StyleButton(filter);
        filter.AddItem(T("全部记录", "All records"));
        filter.AddItem(T("仅看决策", "Choices only"));
        filter.AddItem(T("思考用时 ↓", "Longest decisions"));
        var list = NewList(summary, filter);
        list.Name = "FloorTimeline";
        var jump = Action(T("定位选中记录", "Jump to selected moment"), () => PlaySelected(list));
        jump.Name = "JumpToMoment";
        jump.Disabled = true;
        jump.Visible = !play.Disabled;
        list.ItemSelected += i => jump.Disabled = play.Disabled ||
            activeRoom.HasCombat && events[list.GetItemMetadata((int)i).AsInt32()].Turn <= 0;
        list.ItemActivated += _ => { if (!jump.Disabled) PlaySelected(list); };
        detail.AddChild(jump);
        filter.ItemSelected += i => Fill((int)i);
        Fill(0);
        void Fill(int mode)
        {
            list.Clear();
            jump.Disabled = true;
            var indices = Enumerable.Range(0, events.Count);
            if (mode == 1) indices = indices.Where(i => events[i].Kind is "choice" or "event" or "map" or "shop" or "camp" or "treasure" or "reward");
            if (mode == 2) indices = indices.Where(i => events[i].DecisionSeconds.HasValue).OrderByDescending(i => events[i].DecisionSeconds);
            foreach (int i in indices)
            {
                AddLine(list, Describe(events[i]), events[i].Kind);
                list.SetItemMetadata(list.ItemCount - 1, i);
            }
            if (list.ItemCount == 0)
            {
                AddLine(list, mode == 2 ? T("本层没有思考时间记录。", "No decision times on this floor.") :
                    T("本层没有对应记录。", "No matching records on this floor."), "");
                list.SetItemSelectable(0, false);
            }
        }
        void PlaySelected(ItemList source)
        {
            var selected = source.GetSelectedItems();
            if (selected.Length > 0 && source.IsItemSelectable(selected[0]))
            {
                int indexInRoom = source.GetItemMetadata(selected[0]).AsInt32();
                var mark = events[indexInRoom];
                StartAt(activeRoom, mark.Turn, decision: activeRoom.HasCombat ? CombatDecision(mark) : indexInRoom + 1);
            }
        }
    }

    private sealed record Occurrence(RoomInfo Room, ReplayEvent Event);

    private async void CardTotals()
    {
        if (run == null) return;
        var selected = run;
        int revision = ++viewRevision;
        try
        {
            Clear(detail);
            detail.AddChild(BackToTimeline());
            detail.AddChild(Text(T("正在统计卡牌…", "Reading card records…"), 16, true));
            var all = await Task.Run(() => selected.Rooms.SelectMany(r => ReplayStore.LoadEvents(selected.Id, r.Index)
                .Where(e => !string.IsNullOrWhiteSpace(e.Card))
                .Select(e => new Occurrence(r, e))).ToList());
            if (!IsOpen() || revision != viewRevision) return;
            var groups = all.GroupBy(x => x.Event.Card)
                .OrderByDescending(g => g.Count(x => x.Event.Kind is "play" or "auto"))
                .ThenBy(g => g.Key).ToList();
            Clear(detail);
            detail.AddChild(BackToTimeline());
            var list = NewList(T("全局卡牌统计", "Card totals"));
            list.Name = "CardTotalsList";
            foreach (var g in groups)
            {
                string name = CardName(g);
                int plays = g.Count(x => x.Event.Kind == "play");
                int autos = g.Count(x => x.Event.Kind == "auto");
                int draws = g.Count(x => x.Event.Kind == "draw");
                int damage = g.Where(x => x.Event.Kind == "damage").Sum(x => x.Event.Amount);
                int block = g.Where(x => x.Event.Kind == "block").Sum(x => x.Event.Amount);
                AddLine(list, $"{name}    {T("抽取", "Drawn")} {draws}    {T("手打", "Played")} {plays}   {T("自动", "Auto")} {autos}    {T("扣血", "HP damage")} {damage}    {T("格挡", "Block")} {block}", "play");
            }
            if (groups.Count == 0)
            {
                AddLine(list, T("这局还没有卡牌记录。", "No card effects recorded in this run."), "");
                return;
            }
            void OpenSelected()
            {
                var selection = list.GetSelectedItems();
                if (selection.Length == 1) CardDetail(groups[selection[0]]);
            }
            var inspect = Action(T("查看选中卡牌", "View selected card"), OpenSelected);
            inspect.Name = "ViewCardDetails";
            inspect.Disabled = true;
            list.ItemSelected += _ => inspect.Disabled = false;
            list.ItemActivated += _ => OpenSelected();
            detail.AddChild(inspect);
            list.GrabFocus();
        }
        catch (Exception e) { if (IsOpen() && revision == viewRevision) Error(e); }
    }

    private static string CardName(IGrouping<string, Occurrence> g) =>
        g.FirstOrDefault(x => x.Event.Kind is "play" or "auto" or "draw")?.Event.Label ?? g.Key;

    private Button BackToTimeline()
    {
        var back = Action(T("返回本层时间线", "Back to floor timeline"), Timeline);
        back.Name = "BackToTimeline";
        return back;
    }

    private void CardDetail(IGrouping<string, Occurrence> group)
    {
        if (!GodotObject.IsInstanceValid(Window) || Window.IsQueuedForDeletion() || run == null) return;
        var entries = group.ToList();
        Clear(detail);
        var back = Action(T("返回全局卡牌统计", "Back to card totals"), CardTotals);
        back.Name = "BackToCards";
        detail.AddChild(back);
        var list = NewList(CardName(group));
        list.Name = "CardMomentList";
        foreach (var item in entries)
            AddLine(list, $"{FloorLabel(item.Room)}    {Describe(item.Event)}", item.Event.Kind);
        var jump = Action(T("观看选中记录", "Watch selected moment"), () =>
        {
            var selected = list.GetSelectedItems();
            if (selected.Length == 0) return;
            var target = entries[selected[0]];
            StartAt(target.Room, target.Event.Turn, decision: CombatDecision(target.Event));
        });
        jump.Disabled = true;
        list.ItemSelected += i => jump.Disabled = !entries[(int)i].Room.HasCombat || ReplayStore.CheckCompatibility(run) != null;
        list.ItemActivated += _ => { if (!jump.Disabled) jump.EmitSignal(BaseButton.SignalName.Pressed); };
        detail.AddChild(jump);
    }

    private ItemList NewList(string title, Control? filter = null)
    {
        var heading = Row();
        detail.AddChild(heading);
        var label = Text(title, 18);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        heading.AddChild(label);
        if (filter != null) heading.AddChild(filter);
        var list = new ItemList { AllowReselect = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        Expand(list);
        Typeface(list);
        list.AddThemeConstantOverride("v_separation", 8);
        list.AddThemeStyleboxOverride("panel", Box(Ink, Line, 8, 8));
        detail.AddChild(list);
        return list;
    }

    private static void AddLine(ItemList list, string text, string kind)
    {
        int index = list.AddItem(text);
        list.SetItemTooltip(index, text);
        if (kind is "turn" or "map") list.SetItemCustomFgColor(index, Gold);
        if (kind is "damage" or "block" or "blocked") list.SetItemCustomFgColor(index, Muted);
    }

    private static int? CombatDecision(ReplayEvent e) =>
        e.Kind is "play" or "auto" or "damage" or "blocked" or "block" or "draw" ||
        e.Kind == "choice" && e.ActionKey == "combat" ? e.ActionIndex : null;

    private async void StartAt(RoomInfo target, int turn, bool captureAlbum = false, bool continuous = false, int? decision = null, bool forumAlbum = false)
    {
        if (run == null || busy) return;
        var selected = run;
        try
        {
            string? mismatch = ReplayStore.CheckCompatibility(selected);
            if (mismatch != null) { Message(mismatch); return; }
            if ((captureAlbum || forumAlbum) && DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
            { Message(T("请在图形窗口中生成实机图集。", "Open the graphical game to capture frames.")); return; }
            busy = true;
            Window.Hide();
            var toolbar = new ReplayToolbar(tree, selected, target, continuous);
            toolbar.Show();
            try
            {
                if (!forumAlbum && !captureAlbum) await toolbar.OpenRoomAsync(target, turn, decision);
                if (captureAlbum || forumAlbum)
                {
                    string result;
                    try
                    {
                        if (forumAlbum) result = await toolbar.CaptureForumAsync();
                        else result = await toolbar.CaptureAlbumAsync();
                    }
                    finally
                    {
                        try { await ReplayPlayer.StopAsync(); }
                        finally { toolbar.Remove(); }
                    }
                    busy = false;
                    Window.Show();
                    ReportExport(result);
                    return;
                }
            }
            catch { toolbar.Remove(); throw; }
            busy = false;
            Close();
        }
        catch (Exception e)
        {
            busy = false;
            Window.Show();
            if (e is OperationCanceledException) Message(T("已停止生成图集。", "Gallery capture stopped."));
            else Error(e);
        }
    }

    private async void Export(Func<string> action)
    {
        if (writingFile) return;
        writingFile = true;
        Message(T("正在导出…", "Exporting…"));
        try
        {
            string result = await Task.Run(action);
            if (!IsOpen()) return;
            ReportExport(result);
        }
        catch (Exception e) { if (IsOpen()) Error(e); }
        finally { writingFile = false; }
    }

    private void ReportExport(string result)
    {
        Message($"{T("已导出", "Exported")}  {result}");
        exportedPath = result;
        openFile.Visible = copyPath.Visible = true;
    }

    private async void ExportFilm(RunInfo source, RoomInfo? selected, int targetHeight = 720, float? audioGain = null)
    {
        if (busy || writingFile) return;
        if (!VideoExporter.EncoderAvailable)
        {
            ReplayEncoderDialog.Open(Window, () => ExportFilm(source, selected, targetHeight, audioGain));
            return;
        }
        if (!audioGain.HasValue && SaveManager.Instance.SettingsSave.VolumeMaster <= 0)
        {
            PickFilmVolume(gain => ExportFilm(source, selected, targetHeight, gain));
            return;
        }
        busy = true;
        try
        {
            var film = new ReplayFilm(tree, source, selected == null ? source.Rooms : [selected]);
            Window.Hide();
            string result = await film.WriteAsync(selected == null, targetHeight, audioGain);
            ReportExport(result);
        }
        catch (OperationCanceledException) { Message(T("已取消视频导出。", "Video export cancelled.")); }
        catch (Exception e) { Error(e); }
        finally
        {
            busy = false;
            if (IsOpen()) Window.Show();
        }
    }

    private void PickFilmVolume(Action<float> choose)
    {
        if (Window.GetNodeOrNull<Window>("MutedVideo") is { } existing) { existing.GrabFocus(); return; }
        var dialog = new Window
        {
            Name = "MutedVideo", Title = T("视频声音", "Video audio"), Visible = false,
            Transient = true, Exclusive = true, Borderless = true, ProcessMode = Node.ProcessModeEnum.Always
        };
        Window.AddChild(dialog);
        var panel = Panel(Ink, 16);
        dialog.AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var body = Column(8);
        panel.AddChild(body);
        body.AddChild(Text(T("游戏已静音", "Game audio is muted"), 20));
        var help = Text(T("可以沿用静音设置，也可以在本次导出时播放游戏原声。", "Export a silent video or play game audio for this export."), 15, true);
        help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(help);
        var silent = Action(T("静音导出", "Export muted"), () => Dismiss(0));
        silent.Name = "ExportSilent";
        body.AddChild(silent);
        var sound = Action(T("有声导出（播放音量 25%）", "Export with audio (25% playback volume)"), () => Dismiss(0.25f));
        sound.Name = "ExportWithSound";
        body.AddChild(sound);
        var cancel = Action(T("取消", "Cancel"), () => Dismiss(null));
        cancel.Name = "CancelAudioChoice";
        body.AddChild(cancel);
        dialog.CloseRequested += () => Dismiss(null);
        dialog.WindowInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
            {
                dialog.SetInputAsHandled();
                Dismiss(null);
            }
        };
        dialog.PopupCentered(new Vector2I(Math.Min(430, Window.Size.X - 20), 250));
        silent.GrabFocus();
        void Dismiss(float? gain)
        {
            if (dialog.IsQueuedForDeletion()) return;
            dialog.Hide();
            dialog.QueueFree();
            if (gain.HasValue) choose(gain.Value);
        }
    }

    private void PickFile()
    {
        var dialog = new FileDialog
        {
            Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenFile,
            Title = T("导入回放", "Import replay"), Transient = true, Exclusive = true,
            Filters = ["*.spireplay;Replay the Spire", "*;All files"]
        };
        Window.AddChild(dialog);
        dialog.FileSelected += async path =>
        {
            dialog.QueueFree();
            int attempt = ++importRevision;
            int requestedSelection = selectionRevision;
            Message(T("正在导入…", "Importing…"));
            try
            {
                string id = await Task.Run(() => ReplayStore.Import(path));
                if (IsOpen() && attempt == importRevision &&
                    await ReloadAsync(id, null, requestedSelection) && IsOpen() && attempt == importRevision)
                    Message(T("已导入回放", "Replay imported"));
            }
            catch (Exception e) { if (IsOpen() && attempt == importRevision) Error(e); }
        };
        dialog.Canceled += () => dialog.QueueFree();
        dialog.PopupCenteredRatio(0.85f);
    }

    private void Reveal()
    {
        if (exportedPath == null) return;
        var path = Directory.Exists(exportedPath) ? exportedPath : Path.GetDirectoryName(exportedPath);
        if (path != null) RevealDirectory(path);
    }

    private void RevealDirectory(string path)
    {
        var result = OS.ShellOpen(new Uri(path + Path.DirectorySeparatorChar).AbsoluteUri);
        if (result != Godot.Error.Ok) Message($"{T("无法打开目录", "Could not open folder")}  {path}");
    }

    private void Error(Exception e) => Message(e.GetBaseException().Message);
    private void Message(string text)
    {
        status.Text = text;
        status.TooltipText = text;
        exportedPath = null;
        openFile.Visible = copyPath.Visible = false;
    }
    private bool IsOpen() => GodotObject.IsInstanceValid(Window) && !Window.IsQueuedForDeletion();

    private static string Describe(ReplayEvent e)
    {
        if (e.Kind == "turn" && e.Turn > 0) return T($"第 {e.Turn} 回合", $"Turn {e.Turn}");
        var kind = e.Kind switch
        {
            "play" => T("出牌", "Play"), "auto" => T("自动出牌", "Auto play"),
            "damage" => T("实际扣血", "HP damage"), "blocked" => T("被挡伤害", "Absorbed"),
            "block" => T("获得格挡", "Block gained"), "choice" => T("决策", "Choice"),
            "draw" => T("抽牌", "Draw"), "map" => T("选路", "Route"), "shop" => T("商店", "Shop"),
            "camp" => T("营火", "Camp"), "reward" => T("奖励", "Reward"), "event" => T("事件", "Event"),
            "turn" => T("回合", "Turn"), "treasure" => T("宝箱", "Treasure"), _ => e.Kind
        };
        var turn = e.Turn > 0 ? $"{T("回合", "Turn")} {e.Turn}   " : "";
        var amount = e.Kind is "damage" or "blocked" or "block" ? $"  {e.Amount}" : "";
        var time = e.DecisionSeconds is double seconds ? $"    {seconds:0.0} {T("秒", "s")}" : "";
        return $"{turn}{kind}  ·  {e.Label}{amount}{time}";
    }

    internal static string FloorLabel(RoomInfo room) => T($"第 {room.Floor} 层", $"Floor {room.Floor}");

    internal static string RoomName(string name) => name.ToLowerInvariant() switch
    {
        "monster" or "combat" => T("战斗", "Combat"), "elite" => T("精英", "Elite"),
        "boss" => T("首领", "Boss"), "shop" or "merchant" => T("商店", "Merchant"),
        "event" or "事件" => T("事件", "Event"), "treasure" or "宝箱" => T("宝箱", "Treasure"),
        "商店" => T("商店", "Merchant"), "restsite" or "rest" or "营地" => T("营地", "Rest site"),
        "map" or "地图" => T("地图", "Map"), _ => name
    };

    private static string ResultText(string result) => result.ToLowerInvariant() switch
    {
        "win" or "victory" or "胜利" => T("胜利", "Victory"), "loss" or "defeat" or "战败" => T("战败", "Defeat"),
        "放弃" or "abandoned" => T("已放弃", "Abandoned"), "" or "active" or "in progress" => T("进行中", "In progress"), _ => result
    };
}
