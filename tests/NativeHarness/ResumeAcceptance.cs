using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using SpireReplay;
using SpireReplay.Playback;

namespace ReplayHarness;

internal static class ResumeAcceptance
{
    // The basic record phase leaves an ordinary save in the middle of its first combat.
    internal static async Task ContinueAsync(NMainMenu menu, NGame game)
    {
        var original = ReplayStore.ListRuns().First(r => r.Seed == "REPLAYPROOF");
        var previous = original.Rooms.Last(r => r.HasCombat);
        byte[] bytes = ReplayStore.LoadRoom(original.Id, previous.Index).Combat!;
        await (Task)AccessTools.Method(typeof(NMainMenu), "OnContinueButtonPressedAsync").Invoke(menu, null)!;
        var run = RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("Continue didn't load a native save.");
        if (run.Rng.StringSeed != original.Seed) throw new InvalidDataException("Continue loaded a different fixture; run immediately after record.");
        var who = run.Players.Single();
        await JourneyAcceptance.Wait(() => who.PlayerCombatState?.Phase == PlayerTurnPhase.Play && who.PlayerCombatState.Hand.Cards.Count >= 4
            && !RunManager.Instance.ActionExecutor.IsPaused, "resumed combat");
        var defend = who.PlayerCombatState!.Hand.Cards.First(c => c.TargetType == TargetType.Self && c.CanPlay());
        var play = new PlayCardAction(defend, null);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(play);
        await JourneyAcceptance.Wait(() => play.CompletionTask.IsCompleted, "resumed card");
        await game.ReturnToMainMenu();
        ReplayStore.Flush();
        var history = ReplayStore.ListRuns().Where(r => r.Seed == "REPLAYPROOF").ToArray();
        var now = history.Single();
        var next = now.Rooms.Last(r => r.HasCombat);
        if (now.Id != original.Id || next.Index <= previous.Index || previous.Floor != next.Floor ||
            !ReplayStore.LoadRoom(now.Id, previous.Index).Combat!.SequenceEqual(bytes))
            throw new InvalidDataException("Continuing a saved game changed the previous floor or started a second replay.");
        if (!ReplayStore.LoadEvents(now.Id, next.Index).Any(e => e.Kind == "block" && e.Amount > 0))
            throw new InvalidDataException("The new visit is missing the alternate action.");
        _ = ReplayStore.Export(now.Id);
        GD.Print("REPLAY_RESUME_RECORD_PASS");
    }

    internal static async Task WatchAsync()
    {
        var run = ReplayStore.ListRuns().Single(r => r.Seed == "REPLAYPROOF");
        var visits = run.Rooms.Where(r => r.HasCombat).ToArray();
        if (visits.Length < 2 || visits[0].Floor != visits[1].Floor) throw new InvalidDataException("Two actual visits to the same floor are required.");
        foreach (var room in visits)
        {
            var data = ReplayStore.LoadRoom(run.Id, room.Index);
            await ReplayPlayer.PlayAsync(data.Combat!, 0, data.Events);
            ReplayPlayer.SetSpeed(4);
            await JourneyAcceptance.Wait(() => ReplayPlayer.IsFinished, "resume visit " + room.Index);
            if (!ReplayPlayer.IsVerified || ReplayPlayer.HasError) throw new InvalidDataException(ReplayPlayer.Status);
            await ReplayPlayer.StopAsync();
        }
        GD.Print("REPLAY_RESUME_WATCH_PASS");
    }
}
