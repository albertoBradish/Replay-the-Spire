using System.Diagnostics;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Pooling;
using MegaCrit.Sts2.Core.Runs;

namespace ReplayHarness;

internal static class PoolTrace
{
    private sealed class CardHistory
    {
        internal int Generation;
        internal Queue<string> Calls = new();
    }

    private static readonly Dictionary<NCard, CardHistory> cards = [];
    private static CardHistory History(NCard card)
    {
        if (!cards.TryGetValue(card, out var history)) cards.Add(card, history = new());
        return history;
    }

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(GodotTreeExtensions), nameof(GodotTreeExtensions.QueueFreeSafely)),
            prefix: new HarmonyMethod(typeof(PoolTrace), nameof(QueueFree)));
        harmony.Patch(AccessTools.Method(typeof(NodePool<NCard>), nameof(NodePool<NCard>.Free)),
            prefix: new HarmonyMethod(typeof(PoolTrace), nameof(Free)));
        harmony.Patch(AccessTools.Method(typeof(NCard), nameof(NCard.OnReturnedFromPool)),
            postfix: new HarmonyMethod(typeof(PoolTrace), nameof(Checkout)));
    }

    private static void Checkout(NCard __instance) => History(__instance).Generation++;

    private static void QueueFree(Node node)
    {
        if (node is not NCard card || !GodotObject.IsInstanceValid(card)) return;
        var history = History(card);
        string stack = string.Join(" <- ", new StackTrace(1).GetFrames().Take(10)
            .Select(frame => frame.GetMethod()).Select(method => method?.DeclaringType?.Name + "." + method?.Name));
        history.Calls.Enqueue($"queued epoch={history.Generation} frame={Engine.GetProcessFrames()} floor={RunManager.Instance.DebugOnlyGetState()?.TotalFloor} parent={card.GetParent()?.GetType().Name} model={card.Model?.Id} {stack}");
        while (history.Calls.Count > 6) history.Calls.Dequeue();
    }

    private static void Free(NodePool<NCard> __instance, NCard obj)
    {
        if (!__instance.DebugFreeObjects.Contains(obj)) return;
        var history = History(obj);
        GD.Print($"REPLAY_POOL_TRACE duplicate id={obj.GetInstanceId()} epoch={history.Generation} frame={Engine.GetProcessFrames()}");
        foreach (string call in history.Calls) GD.Print("REPLAY_POOL_TRACE " + call);
    }
}
