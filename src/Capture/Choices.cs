using MegaCrit.Sts2.Core.Nodes.Rewards;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using SpireReplay.Playback;

namespace SpireReplay.Capture;

internal static partial class CaptureHooks
{
    private static long lastSample;
    private static double availableSeconds;
    private static bool wasAvailable;
    private static bool clockStarted;
    private static int nextDecision;
    private static readonly Dictionary<uint, int> DecisionByAction = new();
    private static (string Label, int Index, double? Seconds)? campChoice;
    private static readonly Dictionary<MerchantEntry, (string Label, string Key, int Index, double? Seconds)> ShopAttempts = new();
    private static string[]? rewardLabels;
    private static string[]? rewardKeys;
    private static NCardRewardSelectionScreen? rewardScreen;

    private static void InstallChoices(Action<Type, string, string, bool> hook)
    {
        InstallCrystalSphere(hook);
        hook(typeof(EventSynchronizer), nameof(EventSynchronizer.ChooseLocalOption), nameof(EventChoice), true);
        hook(typeof(MapSelectionSynchronizer), nameof(MapSelectionSynchronizer.PlayerVotedForMapCoord), nameof(MapChosen), true);
        hook(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.PickRelicLocally), nameof(TreasureChoice), true);
        var purchase = AccessTools.DeclaredMethod(typeof(MerchantEntry), "OnTryPurchaseUnsynchronized")
            ?? AccessTools.DeclaredMethod(typeof(MerchantEntry), "OnTryPurchaseWrapper")
            ?? throw new MissingMethodException("MerchantEntry purchase");
        hook(typeof(MerchantEntry), purchase.Name, nameof(ShopStart), true);
        hook(typeof(MerchantEntry), nameof(MerchantEntry.InvokePurchaseCompleted), nameof(ShopSuccess), true);
        hook(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SelectLocalReward), nameof(RewardOpened), true);
        hook(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SkipLocalRewardsSet), nameof(RewardsSkipped), true);
        hook(typeof(RunManager), nameof(RunManager.ProceedFromTerminalRewardsScreen), nameof(TerminalRewardsLeft), true);
        hook(typeof(ActChangeSynchronizer), nameof(ActChangeSynchronizer.SetLocalPlayerReady), nameof(TerminalRewardsLeft), true);
        hook(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.RefreshOptions), nameof(RewardOptions), false);
        hook(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.SyncLocalChoice), nameof(PlayerChoice), true);
    }

    private static void ResetChoices()
    {
        crystalGrid = null;
        crystalCanProceed = false;
        DecisionByAction.Clear();
        nextDecision = 0;
        ShopAttempts.Clear();
        campChoice = null;
        rewardScreen = null;
        rewardLabels = null;
        rewardKeys = null;
        RestartClock();
    }

    private static void RestartClock()
    {
        lastSample = Stopwatch.GetTimestamp();
        availableSeconds = 0;
        wasAvailable = false;
        clockStarted = false;
    }

    // Accumulate foreground decision time at 10 Hz. A missed frame or suspend must
    // not charge the player for an interval whose focus and input state are unknown.
    private static void SampleClock()
    {
        if (!Recording || tree == null) return;
        var now = Stopwatch.GetTimestamp();
        var delta = Stopwatch.GetElapsedTime(lastSample, now).TotalSeconds;
        if (delta < 0.1) return;
        lastSample = now;
        var manager = RunManager.Instance;
        bool available = roomReady && !tree.Paused && !manager.IsPaused && tree.Root.HasFocus();
        if (available && crystalGrid is { IsFinished: true } && !crystalCanProceed &&
            NOverlayStack.Instance?.Peek() is MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen)
            available = false;
        if (available && combatRoom && CombatManager.Instance.IsInProgress)
        {
            var selecting = NOverlayStack.Instance?.Peek() is NCardGridSelectionScreen ||
                MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance?.Ui?.Hand?.IsInCardSelection == true;
            available = selecting || (run!.Players[0].PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && !CombatManager.Instance.PlayerActionsDisabled && !manager.ActionExecutor.IsRunning);
        }
        if (available && wasAvailable && delta <= 0.5) availableSeconds += delta;
        wasAvailable = available;
        clockStarted |= available;
    }

    private static double? TakeTime()
    {
        SampleClock();
        double? seconds = clockStarted ? Math.Round(availableSeconds, 2) : null;
        RestartClock();
        return seconds;
    }

    private static int Turn => CombatManager.Instance.IsInProgress
        ? CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 0 : 0;

    private static int? ExecutingDecision =>
        RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.Id is uint id && DecisionByAction.TryGetValue(id, out var ordinal)
            ? ordinal : null;

    private static void InputQueued(GameAction action)
    {
        if (!Recording || !CombatManager.Instance.IsInProgress) return;
        Observe(() =>
        {
            int? decision = null;
            if (RunManager.Instance.CombatReplayWriter.IsRecordingReplay && action.RecordableToReplay &&
                action is not GenericHookGameAction and not ReadyToBeginEnemyTurnAction && action.Id is uint id)
            {
                decision = ++nextDecision;
                DecisionByAction[id] = decision.Value;
            }
            var label = action switch
            {
                PlayCardAction play => ModelDb.GetById<CardModel>(play.CardModelId).Title,
                EndPlayerTurnAction => "结束回合",
                UsePotionAction use => $"使用药水（栏位 {use.PotionIndex + 1}）",
                DiscardPotionGameAction => "弃置药水",
                _ => null
            };
            if (label == null) return;
            ReplayStore.Add("choice", label, Turn,
                action is PlayCardAction p ? p.CardModelId.ToString() : "", decisionSeconds: TakeTime(),
                actionKey: "combat", actionIndex: decision);
        });
    }

    private static void MapChosen(MapVote? destination)
    {
        if (!Recording || destination == null) return;
        Observe(() =>
        {
            var coord = destination.Value.coord;
            ReplayStore.Add("map", $"前往地图第 {coord.row + 1} 行、第 {coord.col + 1} 列", decisionSeconds: TakeTime(),
                actionKey: $"{coord.col},{coord.row}");
        });
    }

    private static void EventChoice(EventSynchronizer __instance, int index)
    {
        if (!Recording) return;
        Observe(() =>
        {
            var options = __instance.GetLocalEvent().CurrentOptions;
            if ((uint)index < options.Count)
                ReplayStore.Add("event", options[index].Title.GetFormattedText(), decisionSeconds: TakeTime(),
                    actionKey: options[index].TextKey, actionIndex: index);
        });
    }

    private static void RestStarted(RestSiteOption option, ulong _) => Observe(() =>
    {
        if (!Recording) return;
        var options = RunManager.Instance.RestSiteSynchronizer.GetLocalOptions();
        var index = options.ToList().IndexOf(option);
        campChoice = (option.Title.GetFormattedText(), index, TakeTime());
        ReplayStore.Add("camp-open", campChoice.Value.Label, decisionSeconds: campChoice.Value.Seconds,
            actionKey: option.OptionId, actionIndex: index);
    });

    private static void RestFinished(RestSiteOption option, bool success, ulong _) => Observe(() =>
    {
        if (Recording && success && campChoice is { } chosen)
            ReplayStore.Add("camp", chosen.Label, actionKey: option.OptionId, actionIndex: chosen.Index);
        campChoice = null;
    });

    private static void TreasureChoice(TreasureRoomRelicSynchronizer __instance, int? index)
    {
        if (!Recording || __instance.CurrentRelics == null) return;
        Observe(() =>
        {
            var relic = index is int i && (uint)i < __instance.CurrentRelics.Count ? __instance.CurrentRelics[i] : null;
            ReplayStore.Add("treasure", relic?.Title.GetFormattedText() ?? "跳过宝箱遗物", decisionSeconds: TakeTime(),
                actionKey: relic?.Id.ToString() ?? "skip", actionIndex: index ?? -1);
        });
    }

    private static void ShopStart(MerchantEntry __instance, MerchantInventory? inventory)
    {
        if (!Recording) return;
        Observe(() =>
        {
            if (!__instance.IsStocked) return;
            var name = __instance switch
            {
                MerchantCardEntry card => card.CreationResult?.Card.Title,
                MerchantRelicEntry relic => relic.Model?.Title.GetFormattedText(),
                MerchantPotionEntry potion => potion.Model?.Title.GetFormattedText(),
                MerchantCardRemovalEntry => "移除卡牌",
                _ => __instance.GetType().Name
            };
            var key = __instance switch
            {
                MerchantCardEntry card => card.CreationResult?.Card.Id.ToString(),
                MerchantRelicEntry relic => relic.Model?.Id.ToString(),
                MerchantPotionEntry potion => potion.Model?.Id.ToString(),
                MerchantCardRemovalEntry => "remove",
                _ => __instance.GetType().FullName
            };
            ShopAttempts[__instance] = (name ?? "商品", key ?? "", inventory?.AllEntries.ToList().IndexOf(__instance) ?? -1, TakeTime());
            var attempt = ShopAttempts[__instance];
            ReplayStore.Add("shop-open", attempt.Label, decisionSeconds: attempt.Seconds, actionKey: attempt.Key, actionIndex: attempt.Index);
        });
    }

    private static void ShopSuccess(MerchantEntry __instance)
    {
        if (Recording && ShopAttempts.Remove(__instance, out var purchase))
            Observe(() => ReplayStore.Add("shop", purchase.Label, actionKey: purchase.Key, actionIndex: purchase.Index));
    }

    private static void RewardOpened(Reward reward)
    {
        if (!Recording) return;
        Observe(() =>
        {
            int ordinal = 0;
            if (NOverlayStack.Instance?.Peek() is NRewardsScreen screen)
            {
                var offered = screen.FindChildren("*", "", true, false).OfType<NRewardButton>().ToArray();
                ordinal = Array.FindIndex(offered, button => ReferenceEquals(button.Reward, reward)) + 1;
            }
            ReplayStore.Add("reward", reward.Description.GetFormattedText(), amount: ordinal, decisionSeconds: TakeTime(),
                actionKey: RewardChoices.OpenKey(reward), actionIndex: reward.RewardsSetIndex);
        });
    }

    private static void RewardsSkipped()
    {
        if (Recording) Observe(() => ReplayStore.Add("reward", "结束奖励选择", decisionSeconds: TakeTime(), actionKey: "skip-set"));
    }

    private static void TerminalRewardsLeft()
    {
        if (!Recording || NOverlayStack.Instance?.Peek() is not NRewardsScreen screen) return;
        Observe(() =>
        {
            var set = AccessTools.Field(typeof(NRewardsScreen), "_rewardsSet")?.GetValue(screen) as RewardsSet;
            if (set == null) return;
            bool remaining = !set.AllRewardsSuccessfullySelected;
            ReplayStore.Add("reward", remaining ? "跳过剩余奖励" : "结束奖励选择", decisionSeconds: TakeTime(),
                actionKey: remaining ? "skip-set" : "leave-set");
        });
    }

    private static void RewardOptions(NCardRewardSelectionScreen __instance,
        IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> extraOptions)
    {
        if (!Recording) return;
        Observe(() =>
        {
            rewardScreen = __instance;
            rewardLabels = options.Select(c => c.Card.Title)
                .Concat(extraOptions.Select(o => o.Title.GetFormattedText())).ToArray();
            rewardKeys = options.Select(c => $"pick:{c.Card.Id}")
                .Concat(extraOptions.Select(o => $"alternative:{o.OptionId}")).ToArray();
            RestartClock();
        });
    }

    private static void PlayerChoice(PlayerChoiceResult result)
    {
        if (!Recording) return;
        Observe(() =>
        {
            if (result.ChoiceType == PlayerChoiceType.Player) return;
            string label;
            string kind = "choice";
            string key;
            int? index = null;
            if (result.ChoiceType == PlayerChoiceType.Index)
            {
                var indexes = result.AsIndexes();
                index = indexes.Count == 1 ? indexes[0] : null;
                key = $"index:{string.Join(",", indexes)}";
                if (rewardLabels != null && rewardKeys != null && GodotObject.IsInstanceValid(rewardScreen) && rewardScreen!.IsInsideTree())
                {
                    kind = "reward";
                    label = string.Join("、", indexes.Select(i => (uint)i < rewardLabels.Length ? rewardLabels[i] : $"选项 {i + 1}"));
                    if (label.Length == 0) label = "跳过选牌";
                    key = index is int i && (uint)i < rewardKeys.Length ? rewardKeys[i] : "skip-card";
                    index ??= -1;
                }
                else label = indexes.Count == 0 ? "跳过选择" : $"选择 {string.Join("、", indexes.Select(i => i + 1))}";
            }
            else if (result.ChoiceType is PlayerChoiceType.CanonicalCard or PlayerChoiceType.CombatCard
                or PlayerChoiceType.DeckCard or PlayerChoiceType.MutableCard)
            {
                var cards = result.AsCards(result.ChoiceType).ToArray();
                label = string.Join("、", cards.Select(c => c.Title));
                key = result.ChoiceType == PlayerChoiceType.DeckCard && result.ToNetData().deckCards is { } deck
                    ? $"deck:{string.Join(",", deck.Select(c => c.DeckIndex))}"
                    : $"cards:{string.Join(",", cards.Select(c => c.Id))}";
                if (label.Length == 0) label = "未选卡牌";
            }
            else return;
            ReplayStore.Add(kind, label, Turn, decisionSeconds: TakeTime(), actionKey: key, actionIndex: index);
        });
    }
}
