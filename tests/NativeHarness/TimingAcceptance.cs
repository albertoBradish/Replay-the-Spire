using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
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

namespace ReplayHarness;

internal static class TimingAcceptance
{
    internal static async Task RunAsync(NGame game)
    {
        const string seed = "DECISIONCLOCK";
        var tree = game.GetTree();
        var window = tree.Root;
        var initialPause = tree.Paused;
        SaveManager.Instance.Progress.EnableFtues = false;
        var iron = ModelDb.Character<Ironclad>();
#if BETA
        var participant = Player.CreateForNewRun(iron, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1);
        var run = await game.StartNewSingleplayerRun(participant, true, ActModel.GetDefaultList(), [], seed, GameMode.Standard, false);
#else
        var run = await game.StartNewSingleplayerRun(iron, true, ActModel.GetDefaultList(), [], seed, GameMode.Standard);
#endif
        var focusTarget = new Window
        {
            Name = "DecisionClockFocusTest", Title = "Replay timing test", Visible = false, ForceNative = true,
            Size = new Vector2I(200, 90), Transient = true, ProcessMode = Node.ProcessModeEnum.Always
        };
        window.AddChild(focusTarget);
        double Wall() => Time.GetTicksMsec() / 1000.0;
        async Task Wait(Func<bool> condition, string stage, double timeout = 30)
        {
            double until = Wall() + timeout;
            while (!condition())
            {
                if (Wall() >= until) throw new TimeoutException("Decision clock: " + stage);
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
        }
        async Task<double> Hold(double seconds, Func<bool> check, string stage)
        {
            double start = Wall();
            while (Wall() - start < seconds)
            {
                if (!check()) throw new InvalidOperationException("State changed during " + stage);
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            return Wall() - start;
        }
        double focused = 0, background = 0, paused = 0, nested = 0;
        var player = run.Players.Single();
        try
        {
            await CardPileCmd.RemoveFromDeck(player.Deck.Cards.ToArray(), false);
            foreach (var source in new CardModel[] { ModelDb.Card<TrueGrit>(), ModelDb.Card<PommelStrike>(), ModelDb.Card<SwordBoomerang>() })
            {
                var card = run.CreateCard(source, player);
                if (card is TrueGrit) { card.UpgradeInternal(); card.FinalizeUpgradeInternal(); }
                await CardPileCmd.Add(card, PileType.Deck);
            }
            await PotionCmd.TryToProcure<EnergyPotion>(player);
            var map = run.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster).OrderBy(p => p.coord.row).First();
            await RunManager.Instance.EnterMapCoord(map.coord);
            bool CanPlay() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play && !CombatManager.Instance.PlayerActionsDisabled &&
                !RunManager.Instance.ActionExecutor.IsRunning && !RunManager.Instance.ActionExecutor.IsPaused;
            window.GrabFocus();
            await Wait(() => CanPlay() && window.HasFocus() && player.PlayerCombatState?.Hand.Cards.Count == 3, "foreground hand");
            // Reset the recorded decision interval at a real player input.
            var drink = new UsePotionAction(player.Potions.Single(), null, true);
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(drink);
            await Wait(() => drink.CompletionTask.IsCompleted && CanPlay(), "potion settles");
            focused += await Hold(1.2, () => window.HasFocus() && CanPlay(), "foreground input");
            focusTarget.Show();
            focusTarget.GrabFocus();
            await Wait(() => focusTarget.HasFocus() && !window.HasFocus(), "move focus to auxiliary window");
            background = await Hold(2, () => !window.HasFocus(), "unfocused window");
            focusTarget.Hide();
            window.GrabFocus();
            await Wait(() => window.HasFocus() && CanPlay(), "return to foreground");
            focused += await Hold(0.6, () => window.HasFocus() && CanPlay(), "return to play");
            tree.Paused = true;
            paused = await Hold(2, () => window.HasFocus() && tree.Paused, "paused tree");
            tree.Paused = false;
            await Wait(CanPlay, "unpause");
            focused += await Hold(1.2, () => window.HasFocus() && CanPlay(), "unpaused input");
            var grit = player.PlayerCombatState!.Hand.Cards.Single(c => c is TrueGrit);
            var play = new PlayCardAction(grit, null);
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(play);
            var hand = NCombatRoom.Instance!.Ui.Hand;
            await Wait(() => hand.IsInCardSelection && !play.CompletionTask.IsCompleted, "nested hand prompt");
            nested = await Hold(1.8, () => window.HasFocus() && hand.IsInCardSelection && !play.CompletionTask.IsCompleted, "nested card selection");
            var holder = hand.ActiveHolders.Single(h => h.CardModel is PommelStrike);
            AccessTools.Method(hand.GetType(), "OnHolderPressed").Invoke(hand, [holder]);
            if (hand.IsInCardSelection)
                AccessTools.Method(hand.GetType(), "OnSelectModeConfirmButtonPressed").Invoke(hand, [null]);
            await Wait(() => play.CompletionTask.IsCompleted, "resolve hand choice");
        }
        finally
        {
            tree.Paused = initialPause;
            focusTarget.Hide();
            focusTarget.QueueFree();
            await game.ReturnToMainMenu();
        }
        var record = ReplayStore.ListRuns().Where(r => r.Seed == seed).MaxBy(r => r.StartedUtc)!;
        var events = ReplayStore.LoadEvents(record.Id, record.Rooms.Last(r => r.HasCombat).Index);
        var action = events.Single(e => e.Kind == "choice" && e.ActionKey == "combat" && e.Card == ModelDb.Card<TrueGrit>().Id.ToString());
        var answer = events.Single(e => e.Kind == "choice" && e.ActionKey.StartsWith("cards:", StringComparison.Ordinal));
        string measures = JsonSerializer.Serialize(new { focused, background, paused, nested, playDecision = action.DecisionSeconds, nestedDecision = answer.DecisionSeconds });
        File.WriteAllText(Path.Combine(System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST")!, "decision-clock.json"), measures);
        // Three foreground segments each lose at most two 100ms samples at their boundaries.
        if (action.DecisionSeconds is not double playSeconds || playSeconds < focused - 0.8 || playSeconds > focused + 0.7 ||
            answer.DecisionSeconds is not double nestedSeconds || nestedSeconds < nested - 0.5 || nestedSeconds > nested + 0.8)
            throw new InvalidDataException("Unexpected decision-clock durations: " + measures);
        GD.Print("REPLAY_TIMING_PASS " + measures);
    }
}
