using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireReplay;
using SpireReplay.Playback;

namespace ReplayHarness;

/// <summary>Seeded shuffle, manual nested card choice, potion and randomized multi-hit in one ordinary native combat.</summary>
internal static class CombatChoicesAcceptance
{
    const string Seed = "CHOICEREPLAY";
    static async Task Wait(Func<bool> done, string name)
    {
        var tree = NGame.Instance!.GetTree();
        var start = Time.GetTicksMsec();
        while (!done())
        {
            if (Time.GetTicksMsec() - start > 35000) throw new TimeoutException(name + " " + ReplayPlayer.Status);
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
    }
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
        await CardPileCmd.RemoveFromDeck(player.Deck.Cards.ToArray(), false);
        foreach (CardModel source in new CardModel[] { ModelDb.Card<TrueGrit>(), ModelDb.Card<SwordBoomerang>(),
            ModelDb.Card<PommelStrike>(), ModelDb.Card<PommelStrike>(), ModelDb.Card<Discovery>() })
        {
            var model = run.CreateCard(source, player);
            if (model is TrueGrit) { model.UpgradeInternal(); model.FinalizeUpgradeInternal(); }
            await CardPileCmd.Add(model, PileType.Deck);
        }
        await PotionCmd.TryToProcure<EnergyPotion>(player);
        var map = run.Map.GetAllMapPoints().Where(x => x.PointType == MapPointType.Monster).OrderBy(x => x.coord.row).First();
        await RunManager.Instance.EnterMapCoord(map.coord);
        await Wait(() => player.PlayerCombatState?.Hand.Cards.Count == 5 && !RunManager.Instance.ActionExecutor.IsPaused, "opening hand");
        var use = new UsePotionAction(player.Potions.Single(), null, true);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(use);
        await Wait(() => use.CompletionTask.IsCompleted, "potion");
        var grit = player.PlayerCombatState!.Hand.Cards.First(c => c is TrueGrit);
        var play = new PlayCardAction(grit, null);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(play);
        var hand = NCombatRoom.Instance!.Ui.Hand;
        await Wait(() => hand.IsInCardSelection, "native hand prompt");
        var picked = hand.ActiveHolders.Last(h => h.CardModel is PommelStrike);
        uint pickedId = NetCombatCard.FromModel(picked.CardModel!).CombatCardIndex;
        AccessTools.Method(hand.GetType(), "OnHolderPressed").Invoke(hand, [picked]);
        if (hand.IsInCardSelection)
            AccessTools.Method(hand.GetType(), "OnSelectModeConfirmButtonPressed").Invoke(hand, [null]);
        await Wait(() => play.CompletionTask.IsCompleted, "nested choice complete");
        if (!PileType.Exhaust.GetPile(player).Cards.Any(c => NetCombatCard.FromModel(c).CombatCardIndex == pickedId))
            throw new InvalidDataException("A different copy of Pommel Strike was exhausted.");
        var combat = player.Creature.CombatState!;
        var boomerang = new PlayCardAction(player.PlayerCombatState.Hand.Cards.Single(c => c is SwordBoomerang), null);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(boomerang);
        await Wait(() => boomerang.CompletionTask.IsCompleted, "random targets");
        var generated = await JourneyAcceptance.PlayDiscoveryAsync(player.PlayerCombatState.Hand.Cards.OfType<Discovery>().Single());
        if (!player.PlayerCombatState.Hand.Cards.Contains(generated))
            throw new InvalidDataException("Discovery did not add the selected card to the hand.");
        // With only five cards the next draw has to reshuffle the discard pile.
        var end = new EndPlayerTurnAction(player, combat.RoundNumber);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
        await Wait(() => combat.RoundNumber >= 2 && player.PlayerCombatState.Phase == PlayerTurnPhase.Play &&
            player.PlayerCombatState.Hand.Cards.Any(c => c is SwordBoomerang) && !RunManager.Instance.ActionExecutor.IsRunning, "reshuffle");
        var expected = Signature(player);
        await game.ReturnToMainMenu();
        var record = ReplayStore.ListRuns().First(r => r.Seed == Seed);
        var battle = record.Rooms.Last(r => r.HasCombat);
        var data = ReplayStore.LoadRoom(record.Id, battle.Index);
        if (!data.Events.Any(e => e.Kind == "damage") || !data.Events.Any(e => e.Kind == "block"))
            throw new InvalidDataException("Missing damage or block.");
        if (!data.Events.Any(e => e.Kind == "choice" && e.ActionKey == "index:1"))
            throw new InvalidDataException("Missing the middle choice from the Discovery offer.");
        File.WriteAllText(StatePath(), expected);
        GD.Print("REPLAY_CHOICES_RECORD_PASS");
    }

    internal static async Task WatchAsync()
    {
        var record = ReplayStore.ListRuns().First(r => r.Seed == Seed);
        var room = record.Rooms.Last(r => r.HasCombat);
        var data = ReplayStore.LoadRoom(record.Id, room.Index);
        await ReplayPlayer.PlayAsync(data.Combat!, 0, data.Events);
        ReplayPlayer.SetSpeed(3);
        await Wait(() => ReplayPlayer.IsFinished, "nested replay");
        if (!ReplayPlayer.IsVerified) throw new InvalidOperationException(ReplayPlayer.Status);
        if (Signature(RunManager.Instance.DebugOnlyGetState()!.Players.Single()) != File.ReadAllText(StatePath()))
            throw new InvalidDataException("Mismatch after potion, hand selection, Discovery and reshuffle.");
        await ReplayPlayer.StopAsync();
        GD.Print("REPLAY_CHOICES_WATCH_PASS");
    }
    static string StatePath() => Path.Combine(System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST")!, "choices-state.json");
    static string Signature(Player player) => JsonSerializer.Serialize(new {
        hand = player.PlayerCombatState!.Hand.Cards.Select(CombatIdentity),
        exhausted = PileType.Exhaust.GetPile(player).Cards.Select(CombatIdentity),
        potions = player.Potions.Select(p => p.Id.ToString()),
        hp = player.Creature.CurrentHp,
        enemies = player.Creature.CombatState!.HittableEnemies.Select(c => new { id = c.Monster?.Id.ToString(), hp = c.CurrentHp })
    });
    static string CombatIdentity(CardModel card) => $"{card.Id}@{NetCombatCard.FromModel(card).CombatCardIndex}";
}
