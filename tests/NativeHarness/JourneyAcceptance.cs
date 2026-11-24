using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Timeline.Epochs;
using SpireReplay;
using SpireReplay.Playback;

namespace ReplayHarness;

public static class JourneyAcceptance
{
    private const string Seed = "JOURNEYREPLAY";
    private const string RewardSeed = "REWARDREPLAY";
    private static string EvidencePath => Path.Combine(
        System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST")!, "journey-state.json");

    public static async Task RecordAsync(NGame game)
    {
        double clock = Engine.TimeScale;
        try
        {
            Engine.TimeScale = 4;
            var progress = SaveManager.Instance.Progress;
            progress.EnableFtues = false;
            // Start with an ordinary profile that has reached Neow's timeline epoch.
            string neow = EpochModel.GetId<NeowEpoch>();
            if (!progress.IsEpochRevealed(neow))
            {
                progress.ObtainEpoch(neow);
                progress.RevealEpoch(neow);
            }
            foreach (var slot in EpochModel.Get(neow).GetTimelineExpansion())
                if (!progress.HasEpoch(slot.Id))
                    progress.UnlockSlot(slot.Id);
            var hero = ModelDb.Character<Ironclad>();
#if BETA
            var player = Player.CreateForNewRun(hero, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1);
            var run = await game.StartNewSingleplayerRun(player, true, ActModel.GetDefaultList(), [], Seed, GameMode.Standard, false);
#else
            var run = await game.StartNewSingleplayerRun(hero, true, ActModel.GetDefaultList(), [], Seed, GameMode.Standard);
#endif
            var who = run.Players.Single();
            var room = run.CurrentRoom as EventRoom ?? throw new InvalidDataException("Expected Neow's room.");
            var options = room.LocalMutableEvent.CurrentOptions;
            int selected = Enumerable.Range(0, options.Count).Reverse()
                .First(i => !options[i].IsLocked && !options[i].IsProceed && options[i].Relic != null);
            string key = options[selected].TextKey;
            NEventRoom.Instance!.OptionButtonClicked(options[selected], selected);
            await Wait(() => room.LocalMutableEvent.IsFinished, "ancient resolved");
            await RunManager.Instance.EventSynchronizer.AwaitPendingOptionTasks();
            string state = Describe(who);
            GD.Print($"[ReplayHarness] journey event {key} => {state}");
            await NEventRoom.Proceed();
            var next = run.CurrentMapPoint!.Children.Where(p => p.PointType == MapPointType.Monster)
                .OrderBy(p => p.coord.col).First();
            var map = NMapScreen.Instance!;
            var button = map.FindChildren("*", "", true, false).OfType<NMapPoint>()
                .First(node => node.Point.coord == next.coord);
            map.OnMapPointSelectedLocally(button);
            await Wait(() => run.CurrentRoom is CombatRoom && who.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && who.PlayerCombatState.Hand.Cards.Count > 0, "map travel and combat start");
            var combat = who.Creature.CombatState!;
            var attack = who.PlayerCombatState!.Hand.Cards.First(c => c.TargetType == TargetType.AnyEnemy && c.CanPlay());
            var play = new PlayCardAction(attack, combat.HittableEnemies.First());
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(play);
            await Wait(() => play.CompletionTask.IsCompleted, "journey attack");
            for (int n = 0; n < 36 && !who.Creature.IsDead; n++)
            {
                await Wait(() => who.Creature.IsDead || who.PlayerCombatState?.Phase == PlayerTurnPhase.Play &&
                    !RunManager.Instance.ActionExecutor.IsRunning && !CombatManager.Instance.PlayerActionsDisabled,
                    "next player decision");
                if (who.Creature.IsDead) break;
                int round = combat.RoundNumber;
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(who, round));
                await Wait(() => who.Creature.IsDead || combat.RoundNumber > round, "enemy turn");
            }
            Require(who.Creature.IsDead, "natural defeat");
            RunInfo? ended = null;
            await Wait(() => (ended = ReplayStore.ListRuns().FirstOrDefault(r =>
                r.Seed == Seed && r.Result == "战败")) != null, "run outcome recorded");
            string archive = Path.Combine(ReplayStore.ExportsDirectory, ended!.Id + ".spireplay");
            await Wait(() => File.Exists(archive), "automatic completed-run archive");
            var origin = ended.Rooms.First(r => r.HasCheckpoint && ReplayStore.LoadEvents(ended.Id, r.Index)
                .Any(e => e.Kind == "event" && e.ActionKey == key));
            Require(ReplayStore.LoadEvents(ended.Id, origin.Index).Any(e => e.Kind == "map"), "map choice recorded");
            Require(ended.Rooms.Any(r => r.HasCombat), "finished combat recorded");
            File.WriteAllText(EvidencePath, JsonSerializer.Serialize(new Journey(ended.Id, origin.Index, key, state)));
            GD.Print("REPLAY_JOURNEY_RECORD_PASS");
        }
        finally { Engine.TimeScale = clock; }
    }

    public static async Task WatchAsync(NGame game)
    {
        var expected = JsonSerializer.Deserialize<Journey>(File.ReadAllText(EvidencePath))!;
        var info = ReplayStore.ListRuns().First(r => r.Id == expected.Id);
        Require(ReplayStore.CheckCompatibility(info) == null, "portable environment matches");
        var origin = ReplayStore.LoadRoom(info.Id, expected.Room);
        await ReplayPlayer.PlayRoomAsync(origin.Checkpoint!, origin.Events);
        ReplayPlayer.SetSpeed(4);
        await Wait(() => ReplayPlayer.IsFinished, "noncombat playback");
        Require(!ReplayPlayer.HasError && Describe(RunManager.Instance.DebugOnlyGetState()!.Players.Single()) == expected.Player,
            "ancient outcome matches recording " + ReplayPlayer.Status);
        await ReplayPlayer.StopAsync();
        var battle = info.Rooms.Last(r => r.HasCombat);
        await ReplayPlayer.PlayAsync(ReplayStore.LoadRoom(info.Id, battle.Index).Combat!);
        ReplayPlayer.SetSpeed(4);
        await Wait(() => ReplayPlayer.IsFinished, "defeat battle playback", 95);
        Require(ReplayPlayer.IsVerified && !ReplayPlayer.HasError, "journey combat checksums " + ReplayPlayer.Status);
        await ReplayPlayer.StopAsync();
        Require(!ReplayPlayer.IsActive && !RunManager.Instance.IsInProgress && game.MainMenu != null,
            "viewer returned to main menu");
        await ReplayPlayer.PlayRoomAsync(origin.Checkpoint!, origin.Events);
        ReplayPlayer.SetPaused(true);
        Require(ReplayPlayer.IsActive && ReplayPlayer.IsPaused, "quit while viewer is paused");
        // NativeRun's finally calls the normal NGame.Quit. The launcher verifies
        // all protected profile files again after the process has exited.
        GD.Print("REPLAY_JOURNEY_WATCH_PASS");
    }

    public static async Task RewardRecordAsync(NGame game)
    {
        double clock = Engine.TimeScale;
        try
        {
            Engine.TimeScale = 4;
            SaveManager.Instance.Progress.EnableFtues = false;
            var hero = ModelDb.Character<Ironclad>();
#if BETA
            var player = Player.CreateForNewRun(hero, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1);
            var run = await game.StartNewSingleplayerRun(player, true, ActModel.GetDefaultList(), [], RewardSeed, GameMode.Standard, false);
#else
            var run = await game.StartNewSingleplayerRun(hero, true, ActModel.GetDefaultList(), [], RewardSeed, GameMode.Standard);
#endif
            var point = run.Map.GetAllMapPoints().Where(m => m.PointType == MapPointType.Monster)
                .OrderBy(m => m.coord.row).ThenBy(m => m.coord.col).First();
            await RunManager.Instance.EnterMapCoord(point.coord);
            var who = run.Players.Single();
            await Wait(() => who.PlayerCombatState?.Phase == PlayerTurnPhase.Play && who.PlayerCombatState.Hand.Cards.Count > 0,
                "reward encounter playable");
            await WinFightAsync(who);
            await Wait(() => NOverlayStack.Instance?.Peek() is NRewardsScreen, "native reward screen");
            var rewards = (NRewardsScreen)NOverlayStack.Instance!.Peek()!;
            var buttons = rewards.FindChildren("*", "", true, false).OfType<NRewardButton>().ToList();
            var gold = buttons.First(b => b.Reward is GoldReward);
            await PressReward(gold);
            var cardButton = buttons.First(b => b.Reward is CardReward);
            var firstOpen = PressReward(cardButton);
            await Wait(() => NOverlayStack.Instance?.Peek() is NCardRewardSelectionScreen, "card reward opened for skip");
            var cards = (NCardRewardSelectionScreen)NOverlayStack.Instance.Peek()!;
            var alternatives = (IReadOnlyList<MegaCrit.Sts2.Core.Entities.CardRewardAlternatives.CardRewardAlternative>)
                AccessTools.Field(cards.GetType(), "_extraOptions").GetValue(cards)!;
            int skip = alternatives.ToList().FindIndex(a => a.OptionId.Equals("Skip", StringComparison.OrdinalIgnoreCase));
            Require(skip >= 0, "native skip card alternative available");
            AccessTools.Method(cards.GetType(), "OnAlternateRewardSelected").Invoke(cards, [skip]);
            await firstOpen;
            var secondOpen = PressReward(cardButton);
            await Wait(() => NOverlayStack.Instance?.Peek() is NCardRewardSelectionScreen, "card reward reopened");
            cards = (NCardRewardSelectionScreen)NOverlayStack.Instance.Peek()!;
            var picked = ((CardReward)cardButton.Reward!).Cards.First();
            AccessTools.Method(cards.GetType(), "SelectCard").Invoke(cards, [cards.GetCardHolder(picked)]);
            await secondOpen;
            string playerState = Describe(who);
            AccessTools.Method(rewards.GetType(), "OnProceedButtonPressed").Invoke(rewards, [null]);
            await Wait(() => NMapScreen.Instance!.IsOpen, "map after rewards");
            var next = run.CurrentMapPoint!.Children.OrderBy(p => p.coord.col).First();
            var mapButton = NMapScreen.Instance!.FindChildren("*", "", true, false).OfType<NMapPoint>()
                .First(b => b.Point.coord == next.coord);
            NMapScreen.Instance.OnMapPointSelectedLocally(mapButton);
            await Wait(() => run.CurrentMapCoord == next.coord, "map travel after rewards");
            await game.ReturnToMainMenu();
            ReplayStore.Flush();
            var info = ReplayStore.ListRuns().First(r => r.Seed == RewardSeed);
            var room = info.Rooms.First(r => r.HasCombat && ReplayStore.LoadEvents(info.Id, r.Index).Any(e => e.Kind == "reward"));
            var events = ReplayStore.LoadEvents(info.Id, room.Index);
            Require(events.Any(e => e.Kind == "reward" && e.ActionKey.Equals("alternative:Skip", StringComparison.OrdinalIgnoreCase)) &&
                events.Any(e => e.ActionKey == $"pick:{picked.Id}") && events.Any(e => e.Kind == "map"),
                "recorded card skip, picked card and map destination");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(EvidencePath)!, "reward-state.json"),
                JsonSerializer.Serialize(new Journey(info.Id, room.Index, picked.Id.ToString(), playerState)));
            GD.Print("REPLAY_REWARD_RECORD_PASS");
        }
        finally { Engine.TimeScale = clock; }
    }

    internal static async Task WinFightAsync(Player who)
    {
        var combat = who.Creature.CombatState!;
        for (int n = 0; n < 90 && CombatManager.Instance.IsInProgress; n++)
        {
            await Wait(() => !CombatManager.Instance.IsInProgress ||
                who.PlayerCombatState?.Phase == PlayerTurnPhase.Play && !CombatManager.Instance.PlayerActionsDisabled &&
                !RunManager.Instance.ActionExecutor.IsRunning, "reward encounter turn");
            if (!CombatManager.Instance.IsInProgress) break;
            var hand = who.PlayerCombatState!.Hand.Cards;
            var enemy = combat.HittableEnemies.OrderBy(e => e.CurrentHp).FirstOrDefault();
            var attack = hand.FirstOrDefault(c => c.TargetType == TargetType.AnyEnemy && c.CanPlay() && enemy != null);
            var defend = hand.FirstOrDefault(c => c.Type == CardType.Skill && c.CanPlay() && c.TargetType == TargetType.Self);
            var card = attack ?? defend;
            if (card != null)
            {
                var play = new PlayCardAction(card, attack != null ? enemy : null);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(play);
                await Wait(() => play.CompletionTask.IsCompleted || !CombatManager.Instance.IsInProgress, "reward encounter card");
            }
            else
            {
                int turn = combat.RoundNumber;
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(who, turn));
                await Wait(() => !CombatManager.Instance.IsInProgress || combat.RoundNumber > turn, "reward encounter enemy turn");
            }
        }
        Require(!CombatManager.Instance.IsInProgress && !who.Creature.IsDead, "won normal encounter");
    }

    private static Task PressReward(NRewardButton button) =>
        (Task)AccessTools.Method(typeof(NRewardButton), "GetReward").Invoke(button, null)!;

    public static async Task RewardWatchAsync(NGame game)
    {
        var expected = JsonSerializer.Deserialize<Journey>(File.ReadAllText(
            Path.Combine(Path.GetDirectoryName(EvidencePath)!, "reward-state.json")))!;
        var room = ReplayStore.LoadRoom(expected.Id, expected.Room);
        await ReplayPlayer.PlayAsync(room.Combat!, 0, room.Events);
        ReplayPlayer.SetSpeed(4);
        await Wait(() => ReplayPlayer.IsFinished, "reward playback", 95);
        Require(!ReplayPlayer.HasError && ReplayPlayer.IsVerified, "winning combat checksums · " + ReplayPlayer.Status);
        Require(ReplayPlayer.CurrentEvent == ReplayPlayer.EventCount &&
            ReplayPlayer.CurrentDecision == ReplayPlayer.DecisionCount, "played through final recorded decision");
        var actual = RunManager.Instance.DebugOnlyGetState()!.Players.Single();
        Require(Describe(actual) == expected.Player, "same native HP, gold, relics and deck after rewards · " + Describe(actual));
        Require(NMapScreen.Instance!.IsOpen && game.GetTree().Root.HasNode("ReplayDecisionCaption"),
            "recorded destination shown on native map");
        await ReplayPlayer.StopAsync();
        GD.Print("REPLAY_REWARD_WATCH_PASS");
    }

    private static string Describe(Player player) => string.Join("|", player.Creature.CurrentHp, player.Creature.MaxHp,
        player.Gold, string.Join(",", player.Relics.Select(r => r.Id)), string.Join(",", player.Deck.Cards.Select(c => c.Id)));

    internal static async Task Wait(Func<bool> ready, string name, double seconds = 35)
    {
        var tree = NGame.Instance!.GetTree();
        var start = Time.GetTicksMsec();
        while (!ready())
        {
            if (Time.GetTicksMsec() - start > seconds * 1000)
                throw new TimeoutException(name + " · " + ReplayPlayer.Status);
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        GD.Print("[ReplayHarness] " + message);
    }

    private sealed record Journey(string Id, int Room, string Key, string Player);
}
