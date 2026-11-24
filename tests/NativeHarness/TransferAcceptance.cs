using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using SpireReplay;
using SpireReplay.Playback;

namespace ReplayHarness;

internal static class TransferAcceptance
{
    internal static async Task RunAsync(NGame game, bool sameEnvironment = true)
    {
        var path = System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_IMPORT_FILE")
            ?? throw new InvalidOperationException("Select a portable replay from the recording profile.");
        using var archive = ZipFile.OpenRead(path);
        using var index = archive.GetEntry("run.json")!.Open();
        var expected = JsonSerializer.Deserialize<RunInfo>(index)!;
        var original = SHA256.HashData(File.ReadAllBytes(path));
        var menu = game.MainMenu!;
        menu.GetNode<Button>("ReplayTheSpireEntry").EmitSignal(BaseButton.SignalName.Pressed);
        var tree = game.GetTree();
        var window = tree.Root.GetNode<Window>("ReplayTheSpireLibrary");
        bool enteredReplay = false;
        void Observe() => enteredReplay |= ReplayPlayer.IsActive;
        tree.ProcessFrame += Observe;
        var battle = expected.Rooms.First(r => r.HasCombat);
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var import = window.FindChildren("*", "Button", true, false).OfType<Button>().First(b => b.Text is "导入回放" or "Import");
                import.EmitSignal(BaseButton.SignalName.Pressed);
                var choose = window.FindChildren("*", "FileDialog", true, false).OfType<FileDialog>().Single();
                choose.EmitSignal(FileDialog.SignalName.FileSelected, path);
                await JourneyAcceptance.Wait(() => !GodotObject.IsInstanceValid(choose) &&
                    window.FindChild("ExportStatus", true, false) is Label s && (s.Text == "已导入回放" || s.Text == "Replay imported"), "import completion");
                var run = ReplayStore.ListRuns().Single();
                var picker = (OptionButton)window.FindChild("RunPicker", true, false);
                if (picker.ItemCount != 1 || run.Id != expected.Id || run.Build != expected.Build || run.Rooms.Count != expected.Rooms.Count ||
                    (ReplayStore.CheckCompatibility(run) == null) != sameEnvironment)
                    throw new InvalidDataException("Imported run metadata, count or environment failed.");
            }
            if (!sameEnvironment)
            {
                if (expected.Build == GameEnvironment.Build) throw new InvalidDataException("Expected two genuinely different game builds");
                await ReadForeignRun(window, archive, expected, battle);
            }
        }
        finally
        {
            tree.ProcessFrame -= Observe;
            window.EmitSignal(Window.SignalName.CloseRequested);
            await JourneyAcceptance.Wait(() => !GodotObject.IsInstanceValid(window), "close transfer library");
        }
        if (!sameEnvironment)
        {
            if (enteredReplay || ReplayPlayer.IsActive || game.MainMenu == null ||
                !original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))))
                throw new InvalidDataException("Reading the foreign journal entered replay or changed the original file.");
            GD.Print($"REPLAY_FOREIGN_TRANSFER_PASS {expected.Build} -> {GameEnvironment.Build}, one run after two imports, checksum {Convert.ToHexString(original)}");
            return;
        }
        var data = ReplayStore.LoadRoom(expected.Id, battle.Index);
        await ReplayPlayer.PlayAsync(data.Combat!, 0, data.Events);
        ReplayPlayer.SetSpeed(4);
        await JourneyAcceptance.Wait(() => ReplayPlayer.IsFinished, "foreign replay", 100);
        if (!ReplayPlayer.IsVerified || !original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))))
            throw new InvalidDataException("The shared replay failed native state verification.");
        await ReplayPlayer.StopAsync();
        GD.Print("REPLAY_TRANSFER_PASS");
    }

    private static async Task ReadForeignRun(Window window, ZipArchive source, RunInfo run, RoomInfo battle)
    {
        var rooms = (OptionButton)window.FindChild("RoomPicker", true, false);
        int index = run.Rooms.FindIndex(r => r.Index == battle.Index);
        rooms.Select(index);
        rooms.EmitSignal(OptionButton.SignalName.ItemSelected, (long)index);
        var timeline = (ItemList)window.FindChild("FloorTimeline", true, false);
        var originalEvents = new List<ReplayEvent>();
        using (var reader = new StreamReader(source.GetEntry($"room-{battle.Index:D4}.jsonl")!.Open()))
        {
            while (await reader.ReadLineAsync() is { } text)
                if (text.Length > 0) originalEvents.Add(JsonSerializer.Deserialize<ReplayEvent>(text)!);
        }
        int firstPlay = originalEvents.FindIndex(e => e.Kind == "play" && e.Card.Length > 0);
        if (firstPlay < 0 || timeline.ItemCount != originalEvents.Count || !timeline.GetItemText(firstPlay).Contains(originalEvents[firstPlay].Label))
            throw new InvalidDataException("Foreign timeline missing source room records");
        foreach (string name in new[] { "PlayRun", "PlayRoom", "CaptureRunAlbum", "ExportRoomVideo" })
            if (window.FindChild(name, true, false) is not Button { Disabled: true })
                throw new InvalidDataException("Native cross-version operation enabled: " + name);
        if (window.FindChild("CaptureAlbum", true, false) is Button { Visible: true, Disabled: false })
            throw new InvalidDataException("Foreign floor screenshot capture enabled");
        var export = ((MenuButton)window.FindChild("ExportOptions", true, false)).GetPopup();
        if (!export.IsItemDisabled(export.GetItemIndex(3)) || !export.IsItemDisabled(export.GetItemIndex(4)))
            throw new InvalidDataException("Foreign whole-run or HD video enabled");
        timeline.Select(firstPlay);
        timeline.EmitSignal(ItemList.SignalName.ItemSelected, (long)firstPlay);
        if (window.FindChild("JumpToMoment", true, false) is not Button { Disabled: true })
            throw new InvalidDataException("Foreign combat moment was seekable");

        var stats = (Button)window.FindChild("CardTotals", true, false);
        if (stats.Disabled) throw new InvalidDataException("Foreign card statistics disabled");
        stats.EmitSignal(BaseButton.SignalName.Pressed);
        await JourneyAcceptance.Wait(() => window.FindChild("CardTotalsList", true, false) is ItemList { ItemCount: > 0 }, "foreign card totals");
        var totals = (ItemList)window.FindChild("CardTotalsList", true, false);
        int row = Enumerable.Range(0, totals.ItemCount).First(i => totals.GetItemText(i).StartsWith(originalEvents[firstPlay].Label + " ", StringComparison.Ordinal));
        totals.Select(row);
        totals.EmitSignal(ItemList.SignalName.ItemSelected, (long)row);
        ((Button)window.FindChild("ViewCardDetails", true, false)).EmitSignal(BaseButton.SignalName.Pressed);
        await JourneyAcceptance.Wait(() => window.FindChild("CardMomentList", true, false) is ItemList { ItemCount: > 0 }, "foreign card occurrences");
        var moments = (ItemList)window.FindChild("CardMomentList", true, false);
        var sourceCard = originalEvents[firstPlay].Card;
        int expectedCount = run.Rooms.Sum(r => ReplayStore.LoadEvents(run.Id, r.Index).Count(e => e.Card == sourceCard));
        if (moments.ItemCount != expectedCount) throw new InvalidDataException("Foreign card detail did not cover the run");
    }
}
