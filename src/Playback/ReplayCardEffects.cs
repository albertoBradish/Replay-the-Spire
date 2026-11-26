using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Vfx.Cards;

namespace SpireReplay.Playback;

internal static class ReplayCardEffects
{
    private static readonly AccessTools.FieldRef<NCardExhaustVfx, NCard> Card =
        AccessTools.FieldRefAccess<NCardExhaustVfx, NCard>("_cardNode");

    internal static void Install(Harmony harmony) =>
        harmony.Patch(AccessTools.Method(typeof(NCardExhaustVfx), "DelayedFree"),
            prefix: new HarmonyMethod(typeof(ReplayCardEffects), nameof(DelayFree)));

    private static bool DelayFree(NCardExhaustVfx __instance, ref Task __result)
    {
        if (!ReplayPlayer.IsActive) return true;
        __result = FreeWhenOwnedAsync(__instance);
        return false;
    }

    private static async Task FreeWhenOwnedAsync(NCardExhaustVfx effect)
    {
        await Cmd.Wait(2f);
        // The native _ExitTree already returned the card if a replay left the room
        // during the delay. A later room can have borrowed that same card node.
        if (!GodotObject.IsInstanceValid(effect) || !effect.IsInsideTree()) return;
        var card = Card(effect);
        if (GodotObject.IsInstanceValid(card) && effect.IsAncestorOf(card))
            card.QueueFreeSafely();
        effect.QueueFreeSafely();
    }
}
