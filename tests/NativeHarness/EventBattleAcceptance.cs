using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireReplay;
using SpireReplay.Playback;

namespace ReplayHarness;

internal static class EventBattleAcceptance
{
    private const string Seed = "EVENTBATTLE";
    private static string StatePath => Path.Combine(
        System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST")!, "event-battle-state.json");

    internal static async Task RecordAsync(NGame game)
    {
        SaveManager.Instance.Progress.EnableFtues = false;
        var ironclad = ModelDb.Character<Ironclad>();
#if BETA
        var participant = Player.CreateForNewRun(ironclad, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1);
        var run = await game.StartNewSingleplayerRun(participant, true, ActModel.GetDefaultList(), [], Seed, GameMode.Standard, false);
#else
        var run = await game.StartNewSingleplayerRun(ironclad, true, ActModel.GetDefaultList(), [], Seed, GameMode.Standard);
#endif
        var player = run.Players.Single();
        // Fix only the encounter pool and the starting deck. Both are captured by
        // the native save before the ordinary map-event roll and option resolution.
        var roomSet = (RoomSet)AccessTools.Field(typeof(ActModel), "_rooms").GetValue(run.Act)!;
        roomSet.events[roomSet.eventsVisited % roomSet.events.Count] = ModelDb.Event<BattlewornDummy>();
        var odds = run.Odds.UnknownMapPoint;
        odds.MonsterOdds = odds.TreasureOdds = odds.ShopOdds = 0;
        await CardPileCmd.RemoveFromDeck(player.Deck.Cards.ToArray(), false);
        for (int i = 0; i < 5; i++)
            await CardPileCmd.Add(run.CreateCard(ModelDb.Card<Clash>(), player), PileType.Deck);
        var point = run.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Unknown)
            .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
        await RunManager.Instance.EnterMapCoord(point.coord);
        var ev = run.CurrentRoom as EventRoom ?? throw new InvalidDataException("Expected ordinary event roll.");
        if (ev.CanonicalEvent is not BattlewornDummy) throw new InvalidDataException("Expected Battleworn Dummy.");
        var option = ev.LocalMutableEvent.CurrentOptions[1];
        NEventRoom.Instance!.OptionButtonClicked(option, 1);
        await JourneyAcceptance.Wait(() => run.CurrentRoom is CombatRoom && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play,
            "embedded battle start");
        string encounter = ((CombatRoom)run.CurrentRoom!).Encounter.Id.ToString();
        await JourneyAcceptance.WinFightAsync(player);
        await JourneyAcceptance.Wait(() => run.CurrentRoom is EventRoom restored && restored.LocalMutableEvent.IsFinished &&
            player.Deck.Cards.Count(c => c.CurrentUpgradeLevel > 0) == 2, "restored event and its two card upgrades");
        string signature = State(run);
        await game.ReturnToMainMenu();
        ReplayStore.Flush();
        var record = ReplayStore.ListRuns().First(r => r.Seed == Seed);
        var room = record.Rooms.First(r => r.HasCombat && ReplayStore.LoadEvents(record.Id, r.Index)
            .Any(e => e.Kind == "event-battle" && e.ActionKey.EndsWith("|" + encounter, StringComparison.Ordinal)));
        var events = ReplayStore.LoadEvents(record.Id, room.Index);
        if (!events.Any(e => e.Kind == "event" && e.ActionKey == option.TextKey) || !events.Any(e => e.Kind == "event-resume"))
            throw new InvalidDataException("Event entry / return is missing from combat timeline.");
        File.WriteAllText(StatePath, JsonSerializer.Serialize(new Witness(record.Id, room.Index, signature)));
        GD.Print("REPLAY_EVENT_BATTLE_RECORD_PASS");
    }

    internal static async Task WatchAsync()
    {
        var witness = JsonSerializer.Deserialize<Witness>(File.ReadAllText(StatePath))!;
        var room = ReplayStore.LoadRoom(witness.Run, witness.Room);
        await ReplayPlayer.PlayAsync(room.Combat!, 0, room.Events);
        ReplayPlayer.SetSpeed(4);
        await JourneyAcceptance.Wait(() => ReplayPlayer.IsFinished, "event battle replay", 95);
        if (!ReplayPlayer.IsVerified || ReplayPlayer.HasError ||
            State(RunManager.Instance.DebugOnlyGetState()!) != witness.State)
            throw new InvalidDataException("Embedded combat or resumed event result differed: " + ReplayPlayer.Status);
        await ReplayPlayer.SeekTurnAsync(1);
        await JourneyAcceptance.Wait(() => ReplayPlayer.IsPaused || ReplayPlayer.IsFinished, "seek into embedded encounter");
        if (ReplayPlayer.HasError || RunManager.Instance.DebugOnlyGetState()?.CurrentRoom is not CombatRoom ||
            ReplayPlayer.CurrentTurn != 1)
            throw new InvalidDataException("Could not seek to the embedded battle's first decision.");
        await ReplayPlayer.StopAsync();
        GD.Print("REPLAY_EVENT_BATTLE_WATCH_PASS");
    }

    private static string State(RunState run)
    {
        var ev = (EventRoom)run.CurrentRoom!;
        var player = run.Players.Single();
        return string.Join('|', ev.ModelId, ev.LocalMutableEvent.Description?.GetFormattedText(),
            player.Creature.CurrentHp, player.Gold, string.Join(',', player.Deck.Cards.Select(c => $"{c.Id}/{c.CurrentUpgradeLevel}")));
    }

    private sealed record Witness(string Run, int Room, string State);
}
