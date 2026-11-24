using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Runs;

namespace SpireReplay.Playback;

internal static class RoomCardGrid
{
    private static readonly AsyncLocal<bool> opening = new();
    private static readonly FieldInfo optionsField = AccessTools.Field(typeof(NCardGridSelectionScreen), "_cards");
    private static readonly FieldInfo gridField = AccessTools.Field(typeof(NCardGridSelectionScreen), "_grid");

    internal static void Install(Harmony harmony)
    {
        foreach (var name in new[] { nameof(CardSelectCmd.FromSimpleGridForRewards), nameof(CardSelectCmd.FromSimpleGrid), nameof(CardSelectCmd.FromDeckGeneric) })
            harmony.Patch(AccessTools.Method(typeof(CardSelectCmd), name), prefix: new(typeof(RoomCardGrid), nameof(Begin)),
                postfix: new(typeof(RoomCardGrid), nameof(End)));
        harmony.Patch(AccessTools.Method(typeof(CardSelectCmd), "ShouldSelectLocalCard"),
            postfix: new(typeof(RoomCardGrid), nameof(UseNativeScreen)));
        harmony.Patch(AccessTools.Method(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen.CardsSelected)),
            postfix: new(typeof(RoomCardGrid), nameof(DriveNativeScreen)));
    }

    // The game's async card command carries this scope into its own continuations. Combat
    // and other types of card selectors keep using their normal choice synchronization.
    private static void Begin(MethodBase __originalMethod, ref bool __state)
    {
        __state = opening.Value;
        if (RoomChoiceAnswers.Current is not { } scope || !scope.Pending.TryPeek(out var answer)) return;
        var expected = __originalMethod.Name == nameof(CardSelectCmd.FromDeckGeneric) ? "deck:" : "index:";
        opening.Value = answer.Key.StartsWith(expected, StringComparison.Ordinal) && answer.Key.Length > expected.Length;
    }

    private static void End(bool __state)
    {
        if (opening.Value != __state) opening.Value = __state;
    }

    private static void UseNativeScreen(ref bool __result)
    {
        if (opening.Value && RoomChoiceAnswers.Current != null) __result = true;
    }

    private static void DriveNativeScreen(NCardGridSelectionScreen __instance, ref Task<IEnumerable<CardModel>> __result)
    {
        if (opening.Value && RoomChoiceAnswers.Current is { } scope && scope.Pending.TryPeek(out var answer))
            __result = SelectAsync(__instance, __result, scope, answer);
    }

    private static async Task<IEnumerable<CardModel>> SelectAsync(NCardGridSelectionScreen screen,
        Task<IEnumerable<CardModel>> completion, RoomChoiceAnswers.Sequence scope, RoomChoiceAnswers.Entry answer)
    {
        var options = (IReadOnlyList<CardModel>)optionsField.GetValue(screen)!;
        var grid = (NCardGrid)gridField.GetValue(screen)!;
        var prefs = (CardSelectorPrefs)AccessTools.Field(screen.GetType(), "_prefs").GetValue(screen)!;
        var source = answer.Key.StartsWith("deck:", StringComparison.Ordinal) ?
            RunManager.Instance.DebugOnlyGetState()!.Players.Single().Deck.Cards : options;
        var indices = RoomChoiceAnswers.Indices(answer);
        if (indices.Length < prefs.MinSelect || indices.Length > prefs.MaxSelect || indices.Distinct().Count() != indices.Length ||
            indices.Any(i => (uint)i >= source.Count || !options.Contains(source[i])))
            throw new InvalidDataException("记录的卡牌不在当前候选中。");
        var chosen = indices.Select(i => source[i]).ToArray();
        var tree = screen.GetTree();
        while (!grid.CurrentlyDisplayedCardHolders.Any(h => h.CardModel != null && h.IsVisibleInTree()))
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree()) throw new OperationCanceledException();
        }
        await scope.PresentGrid(answer.Index, () =>
        {
            if (chosen.Length > 0) RevealCard(grid, chosen[0]);
            foreach (var card in chosen) grid.HighlightCard(card);
        }, string.Join("、", chosen.Select(c => c.Title)));

        var click = AccessTools.Method(screen.GetType(), "OnCardClicked", [typeof(CardModel)]);
        foreach (var card in chosen)
        {
            if (completion.IsCompleted) throw new InvalidDataException("选牌界面提前关闭。");
            grid.UnhighlightCard(card);
            click.Invoke(screen, [card]);
        }
        var pressed = new HashSet<NConfirmButton>();
        bool autoConfirm = screen is NSimpleCardSelectScreen && !prefs.RequireManualConfirmation && chosen.Length == prefs.MaxSelect;
        while (!autoConfirm && !completion.IsCompleted && pressed.Count < 2 &&
            GodotObject.IsInstanceValid(screen) && screen.IsInsideTree())
        {
            var button = screen.FindChildren("*", "", true, false).OfType<NConfirmButton>()
                .FirstOrDefault(b => !pressed.Contains(b) && b.IsVisibleInTree() && b.IsEnabled);
            if (button == null)
            {
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                continue;
            }
            pressed.Add(button);
            button.EmitSignal(NClickableControl.SignalName.Released, button);
        }
        // The native completion source is signalled during the click, but CardsSelected()
        // can resume on the following Godot frame after the overlay has already closed.
        var result = (await completion).ToArray();
        if (result.Length != chosen.Length || !result.ToHashSet().SetEquals(chosen))
            throw new InvalidDataException("选中的卡牌与记录不同。");
        scope.Pending.Dequeue();
        return result;
    }

    private static void RevealCard(NCardGrid grid, CardModel card)
    {
        if (grid.GetCardHolder(card) is { } holder && grid.GetGlobalRect().HasPoint(holder.GetGlobalRect().GetCenter())) return;
        var type = typeof(NCardGrid);
        var displayed = (List<CardModel>)AccessTools.Field(type, "_cards").GetValue(grid)!;
        int columns = (int)AccessTools.Property(type, "Columns").GetValue(grid)!;
        int index = displayed.IndexOf(card);
        if (index < 0 || columns <= 0) return;
        var size = (Vector2)AccessTools.Field(type, "_cardSize").GetValue(grid)!;
        float padding = (float)AccessTools.Property(type, "CardPadding").GetValue(grid)!;
        float bottom = (float)AccessTools.Property(type, "ScrollLimitBottom").GetValue(grid)!;
        float top = (float)AccessTools.Property(type, "ScrollLimitTop").GetValue(grid)!;
        grid.SetScrollPosition(Mathf.Clamp((grid.Size.Y - size.Y) / 2 - index / columns * (size.Y + padding), bottom, top));
        AccessTools.Method(type, "AllocateCardHolders").Invoke(grid, null);
    }
}
