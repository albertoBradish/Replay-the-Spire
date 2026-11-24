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
    internal sealed record Entry(int Index, string Key);
    internal sealed record Sequence(Queue<Entry> Pending, Func<int, Action, string, Task> PresentGrid);
    private static readonly AsyncLocal<Sequence?> answers = new();
    internal static Sequence? Current => ReplayPlayer.IsActive && !CombatManager.Instance.IsInProgress ? answers.Value : null;

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.WaitForRemoteChoice)),
            prefix: new HarmonyMethod(typeof(RoomChoiceAnswers), nameof(Answer)));
        RoomCardGrid.Install(harmony);
    }

    internal static async Task<bool> ApplyAsync(IReadOnlyList<Entry> keys, Func<int, Action, string, Task> presentGrid, Func<Task<bool>> action)
    {
        if (keys.Any(e => !e.Key.StartsWith("index:", StringComparison.Ordinal) &&
            !e.Key.StartsWith("deck:", StringComparison.Ordinal)))
            return false;
        var previous = answers.Value;
        var queue = new Queue<Entry>(keys);
        answers.Value = new(queue, presentGrid);
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
        if (Current is not { } sequence)
            return;
        if (!sequence.Pending.TryDequeue(out var entry))
            throw new InvalidDataException("该节点缺少选牌记录。");
        int[] selected = Indices(entry);
        NetPlayerChoiceResult answer;
        if (entry.Key.StartsWith("deck:", StringComparison.Ordinal))
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

    internal static int[] Indices(Entry entry)
    {
        var list = entry.Key[(entry.Key.IndexOf(':') + 1)..];
        return list.Length == 0 ? [] : list.Split(',').Select(value => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray();
    }
}
