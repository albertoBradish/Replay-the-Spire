using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.ValueProps;
using SpireReplay;

namespace ReplayHarness;

internal static class RewardDamageAcceptance
{
    private const string PlayerSeed = "4R40MZL9NVRQ";
    private static Task Wait(Func<bool> ready, string name) => JourneyAcceptance.Wait(ready, name);

    internal static async Task RunAsync(NGame game)
    {
        var originalSpeed = Engine.TimeScale;
        Engine.TimeScale = 3;
        SaveManager.Instance.Progress.EnableFtues = false;
        try
        {
            var (player, enemy) = await Start(game, PlayerSeed, twin: false);
            if (enemy.Monster?.Id.Entry != "NIBBIT" || enemy.CurrentHp != 42)
                throw new InvalidDataException($"Expected the 42 HP Nibbit, saw {enemy.Monster?.Id} at {enemy.CurrentHp} HP.");
            var actualLoss = new List<int> { await Play<Bash>(player, enemy), await Play<StrikeIronclad>(player, enemy) };
            var combat = player.Creature.CombatState!;
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, combat.RoundNumber));
            await Wait(() => combat.RoundNumber == 2 && HasHand(player), "second hand");
            for (int i = 0; i < 3; i++) actualLoss.Add(await Play<StrikeIronclad>(player, enemy));
            if (!actualLoss.SequenceEqual(new[] { 8, 9, 9, 9, 7 }) || enemy.CurrentHp != 0)
                throw new InvalidDataException("Native player fixture changed: " + string.Join(",", actualLoss));
            var original = await Finish(game, PlayerSeed);
            var hits = original.Where(e => e.Kind == "damage").ToArray();
            if (!hits.Select(e => e.Amount).SequenceEqual(actualLoss) ||
                !hits.Select(e => e.ActionIndex).SequenceEqual(new int?[] { 1, 2, 4, 5, 6 }) ||
                hits[0].Card != "CARD.BASH" || hits.Skip(1).Any(e => e.Card != "CARD.STRIKE_IRONCLAD") ||
                hits.Skip(1).Sum(e => e.Amount) != 34)
                throw new InvalidDataException("The terminal Strike did not receive its seven actual HP at decision 6: " + JsonSerializer.Serialize(hits));
            foreach (var hit in hits)
                if (original.Count(e => e.Kind == "play" && e.Card == hit.Card && e.ActionIndex == hit.ActionIndex) != 1)
                    throw new InvalidDataException("Damage was assigned to a different card play.");

            const string secondSeed = "REPLAYPERF";
            (player, enemy) = await Start(game, secondSeed, twin: true);
            // Compose a precise native combat state. The first Twin Strike hit is completely
            // blocked. The second loses one to block, removes two actual HP and overkills by two.
            enemy.SetCurrentHpInternal(4);
            await CreatureCmd.Damage(new BlockingPlayerChoiceContext(), enemy, 2m,
                ValueProp.Unpowered | ValueProp.Unblockable, player.Creature, null, null);
            enemy.GainBlockInternal(6);
            if (enemy.Block != 6 || enemy.CurrentHp != 2 || await Play<TwinStrike>(player, enemy) != 2)
                throw new InvalidDataException("Shielded twin-strike fixture did not reach its expected state.");
            var multi = await Finish(game, secondSeed);
            var twin = multi.Single(e => e.Kind == "play" && e.Card == "CARD.TWIN_STRIKE");
            var related = multi.Where(e => e.Card == twin.Card && e.ActionIndex == twin.ActionIndex).ToArray();
            if (twin.ActionIndex == null || !related.Where(e => e.Kind == "blocked").Select(e => e.Amount).SequenceEqual(new[] { 5, 1 }) ||
                !related.Where(e => e.Kind == "damage").Select(e => e.Amount).SequenceEqual(new[] { 2 }) ||
                multi.Count(e => e.Kind == "damage" && e.Card == "" && e.ActionIndex == null && e.Amount == 2) != 1 ||
                multi.Where(e => e.Kind == "damage").Sum(e => e.Amount) != 4)
                throw new InvalidDataException("Incorrect per-hit attribution: " + JsonSerializer.Serialize(multi.Where(e => e.Kind is "damage" or "blocked")));
            GD.Print("REPLAY_DAMAGE_RECORD_PASS Bash=8 Strike=9,9,9,7 TwinStrike=2 blocked=5,1 unrelated=2");
        }
        finally { Engine.TimeScale = originalSpeed; }
    }

    private static async Task<(Player, Creature)> Start(NGame game, string seed, bool twin)
    {
        var hero = ModelDb.Character<Ironclad>();
#if BETA
        var participant = Player.CreateForNewRun(hero, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1);
        var run = await game.StartNewSingleplayerRun(participant, true, ActModel.GetDefaultList(), [], seed, GameMode.Standard, false);
#else
        var run = await game.StartNewSingleplayerRun(hero, true, ActModel.GetDefaultList(), [], seed, GameMode.Standard);
#endif
        var player = run.Players.Single();
        if (twin)
        {
            await CardPileCmd.RemoveFromDeck(player.Deck.Cards.ToArray(), false);
            await CardPileCmd.Add(run.CreateCard(ModelDb.Card<TwinStrike>(), player), PileType.Deck);
        }
        // The player's first combat is the start node. GetAllMapPoints enumerates the paths
        // after it. Read the generated map so coordinate layout changes don't affect this case.
        var map = run.Map.StartingMapPoint;
        if (twin || map.PointType != MapPointType.Monster)
            map = run.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster)
                .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
        await RunManager.Instance.EnterMapCoord(map.coord);
        await Wait(() => HasHand(player), "damage fixture opening hand");
        return (player, player.Creature.CombatState!.HittableEnemies.Single());
    }

    private static bool HasHand(Player p) => p.PlayerCombatState?.Phase == PlayerTurnPhase.Play &&
        p.PlayerCombatState.Hand.Cards.Count > 0 && !RunManager.Instance.ActionExecutor.IsPaused;

    private static async Task<int> Play<T>(Player player, Creature target) where T : CardModel
    {
        var card = player.PlayerCombatState!.Hand.Cards.First(c => c is T && c.CanPlay());
        int before = target.CurrentHp;
        var action = new PlayCardAction(card, target);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
        await Wait(() => action.CompletionTask.IsCompleted, card.Id.ToString());
        return before - target.CurrentHp;
    }

    private static async Task<List<ReplayEvent>> Finish(NGame game, string seed)
    {
        await Wait(() => !CombatManager.Instance.IsInProgress, "terminal reward state");
        ReplayStore.Flush();
        var info = ReplayStore.ListRuns().First(r => r.Seed == seed);
        var events = ReplayStore.LoadEvents(info.Id, info.Rooms.Last(r => r.HasCombat).Index);
        await game.ReturnToMainMenu();
        return events;
    }
}
