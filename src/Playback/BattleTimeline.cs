namespace SpireReplay.Playback;

internal sealed class BattleTimeline
{
    internal ReplayEvent? Battle { get; }
    internal ReplayEvent[] Before { get; } = [];
    internal ReplayEvent[] After { get; } = [];

    internal BattleTimeline(IReadOnlyList<ReplayEvent>? events)
    {
        if (events == null) return;
        int marker = -1;
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i].Kind != "event-battle") continue;
            marker = i;
            Battle = events[i];
            break;
        }
        if (marker >= 0)
            Before = events.Take(marker).Where(e => e.Kind is "event" or "choice" or "reward").ToArray();
        After = events.Skip(marker + 1).SkipWhile(e => e.Kind is not ("reward" or "map" or "event-resume"))
            .Where(e => e.Turn == 0 && e.Kind is "reward" or "map" or "choice" or "event" or "event-resume").ToArray();
    }
}
