using HarmonyLib;
using System.Globalization;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace SpireReplay.Playback;

internal sealed class RewardChoices
{
    private Task? cardPicker;
    internal static bool Visible => NOverlayStack.Instance?.Peek() is NRewardsScreen;

    internal static string OpenKey(Reward reward)
    {
        string? offer = reward switch
        {
            GoldReward gold => gold.Amount.ToString(CultureInfo.InvariantCulture),
            PotionReward potion => potion.Potion?.Id.ToString(),
            RelicReward relic => relic.Relic?.Id.ToString(),
            CardReward cards => string.Join(',', cards.Cards.Select(card => $"{card.Id}/{card.CurrentUpgradeLevel}")),
            _ => null
        };
        string key = $"open:{reward.GetType().FullName}";
        return offer == null ? key : key + "|" + offer;
    }

    internal async Task<bool> ApplyAsync(ReplayEvent entry, ReplayEvent? next)
    {
        if (cardPicker?.IsCompleted == true)
        {
            await cardPicker;
            cardPicker = null;
        }
        var top = NOverlayStack.Instance?.Peek();
        if (top is NCardRewardSelectionScreen cards && cardPicker != null)
            return AnswerCard(cards, entry);
        if (top is not NRewardsScreen screen || cardPicker != null)
            return false;
        var set = AccessTools.Field(typeof(NRewardsScreen), "_rewardsSet")?.GetValue(screen) as RewardsSet;
        if (set == null)
            return false;
        if (entry.ActionKey == "leave-set")
            return set.AllRewardsSuccessfullySelected;
        if (RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(set))
            return false;
        if (entry.ActionKey == "skip-set")
        {
            if (set.DisallowSkipping)
                return false;
            RunManager.Instance.RewardsSetSynchronizer.SkipLocalRewardsSet();
            return true;
        }
        if (!entry.ActionKey.StartsWith("open:", StringComparison.Ordinal))
            return false;
        var offered = screen.FindChildren("*", "", true, false).OfType<NRewardButton>().ToArray();
        bool Matches(NRewardButton b) => b.IsEnabled && b.Reward is { SuccessfullySelected: false } reward &&
            reward.RewardsSetIndex == entry.ActionIndex && MatchesOffer(entry, reward);
        NRewardButton? button;
        if (entry.Amount > 0)
        {
            button = offered.ElementAtOrDefault(entry.Amount - 1);
            if (button == null || !Matches(button)) return false;
        }
        else
        {
            // Older recordings contain the reward category and offer identity, but no button ordinal.
            var matches = offered.Where(Matches).Take(2).ToArray();
            if (matches.Length == 0 || matches.Length > 1 &&
                (matches[0].Reward is not PotionReward || !entry.ActionKey.Contains('|'))) return false;
            button = matches[0];
        }
        if (button.Reward is CardReward && (next?.Kind != "reward" ||
            !(next.ActionKey.StartsWith("pick:", StringComparison.Ordinal) ||
              next.ActionKey.StartsWith("alternative:", StringComparison.Ordinal) || next.ActionKey == "skip-card")))
            return false;
        if (button.Reward is not (CardReward or GoldReward or PotionReward or RelicReward))
            return false;
        if (button.Reward is not CardReward && next?.Kind == "choice")
            return false;
        if (AccessTools.Method(typeof(NRewardButton), "GetReward")?.Invoke(button, null) is not Task opened)
            return false;
        if (button.Reward is CardReward)
        {
            cardPicker = opened;
            // Closing a room can cancel the card-selection screen while a pick is outstanding.
            _ = opened.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        else
            await opened;
        return true;
    }

    private static bool MatchesOffer(ReplayEvent entry, Reward reward)
    {
        if (entry.ActionKey.Contains('|'))
            return entry.ActionKey == OpenKey(reward);
        // Earlier files recorded only the reward class and the localized offer.
        return reward.GetType().FullName == entry.ActionKey[5..] &&
            (reward is CardReward || entry.Label == reward.Description.GetFormattedText());
    }

    private static bool AnswerCard(NCardRewardSelectionScreen screen, ReplayEvent entry)
    {
        if (AccessTools.Field(typeof(NCardRewardSelectionScreen), "_completionSource")?.GetValue(screen)
                is not TaskCompletionSource<int?> source || source.Task.IsCompleted)
            return false;
        if (entry.ActionKey == "skip-card")
            return source.TrySetResult(null);
        var cards = AccessTools.Field(typeof(NCardRewardSelectionScreen), "_options")?.GetValue(screen)
            as IReadOnlyList<CardCreationResult>;
        var alternatives = AccessTools.Field(typeof(NCardRewardSelectionScreen), "_extraOptions")?.GetValue(screen)
            as IReadOnlyList<CardRewardAlternative>;
        if (cards != null && entry.ActionIndex is int index && index >= 0)
        {
            if (index < cards.Count && $"pick:{cards[index].Card.Id}" == entry.ActionKey)
                return source.TrySetResult(index);
            int extra = index - cards.Count;
            if (alternatives != null && (uint)extra < alternatives.Count &&
                $"alternative:{alternatives[extra].OptionId}" == entry.ActionKey)
                return source.TrySetResult(index);
        }
        // A modified card pool may differ; close its prompt and show the recorded choice.
        source.TrySetResult(null);
        return false;
    }
}
