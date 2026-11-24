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
    internal static async Task RunAsync(NGame game)
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
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var import = window.FindChildren("*", "Button", true, false).OfType<Button>().First(b => b.Text is "导入回放" or "Import");
            import.EmitSignal(BaseButton.SignalName.Pressed);
            var choose = window.FindChildren("*", "FileDialog", true, false).OfType<FileDialog>().Single();
            choose.EmitSignal(FileDialog.SignalName.FileSelected, path);
            await JourneyAcceptance.Wait(() => !GodotObject.IsInstanceValid(choose) &&
                window.FindChild("ExportStatus", true, false) is Label s && (s.Text == "已导入回放" || s.Text == "Replay imported"), "import completion");
            var run = ReplayStore.ListRuns().Single();
            if (run.Id != expected.Id || run.Rooms.Count != expected.Rooms.Count || ReplayStore.CheckCompatibility(run) != null)
                throw new InvalidDataException("Imported run metadata or environment failed.");
        }
        window.EmitSignal(Window.SignalName.CloseRequested);
        await JourneyAcceptance.Wait(() => !GodotObject.IsInstanceValid(window), "close transfer library");
        var battle = expected.Rooms.First(r => r.HasCombat);
        var data = ReplayStore.LoadRoom(expected.Id, battle.Index);
        await ReplayPlayer.PlayAsync(data.Combat!, 0, data.Events);
        ReplayPlayer.SetSpeed(4);
        await JourneyAcceptance.Wait(() => ReplayPlayer.IsFinished, "foreign replay", 100);
        if (!ReplayPlayer.IsVerified || !original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))))
            throw new InvalidDataException("The shared replay failed native state verification.");
        await ReplayPlayer.StopAsync();
        GD.Print("REPLAY_TRANSFER_PASS");
    }
}
