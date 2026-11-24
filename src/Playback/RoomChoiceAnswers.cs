using System.Globalization;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace SpireReplay.Playback;

internal static class RoomChoiceAnswers
{
    private static readonly AsyncLocal<Queue<string>?> answers = new();

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.WaitForRemoteChoice)),
            prefix: new HarmonyMethod(typeof(RoomChoiceAnswers), nameof(Answer)));
    }

    internal static async Task<bool> ApplyAsync(IReadOnlyList<string> keys, Func<Task<bool>> action)
    {
        if (keys.Any(key => !key.StartsWith("index:", StringComparison.Ordinal) &&
            !key.StartsWith("deck:", StringComparison.Ordinal)))
            return false;
        var previous = answers.Value;
        var queue = new Queue<string>(keys);
        answers.Value = queue;
        try
        {
            bool success = await action();
            if (success && queue.Count > 0)
                throw new InvalidDataException("房间的选牌次数与记录不同。");
            return success;
        }
        finally
        {
            answers.Value = previous;
        }
    }

    private static void Answer(PlayerChoiceSynchronizer __instance, Player player, uint choiceId)
    {
        if (!ReplayPlayer.IsActive || CombatManager.Instance.IsInProgress || answers.Value is not { } queue)
            return;
        if (!queue.TryDequeue(out var value))
            throw new InvalidDataException("该节点缺少选牌记录。");
        string[] parts = value.Split(':', 2);
        int[] selected = parts[1].Length == 0 ? [] : parts[1].Split(',')
            .Select(value => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray();
        NetPlayerChoiceResult answer;
        if (parts[0] == "deck")
        {
            if (selected.Any(index => (uint)index >= player.Deck.Cards.Count))
                throw new InvalidDataException("牌组顺序与记录不同。");
            answer = new() { type = PlayerChoiceType.DeckCard,
                deckCards = selected.Select(index => NetDeckCard.FromModel(player.Deck.Cards[index])).ToList() };
        }
        else
            answer = new() { type = PlayerChoiceType.Index, indexes = selected.ToList() };
        __instance.ReceiveReplayChoice(player, choiceId, answer);
    }
}
