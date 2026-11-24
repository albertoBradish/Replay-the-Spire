using System.Text.Json.Serialization;

namespace SpireReplay;

public sealed class RunInfo
{
    public int Format { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Character { get; set; } = "";
    public string Build { get; set; } = "";
    public string Mods { get; set; } = "";
    public string Seed { get; set; } = "";
    public string Result { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<RoomInfo> Rooms { get; set; } = [];
}

public sealed record RoomInfo(int Index, int Floor, string Name) { public bool HasCombat { get; set; } public bool HasCheckpoint { get; set; } }
public sealed record ReplayEvent(string Kind, string Label, int Turn = 0, string Card = "",
    int Amount = 0, double? DecisionSeconds = null, string ActionKey = "", int? ActionIndex = null);
public sealed record RoomData(List<ReplayEvent> Events, byte[]? Combat, byte[]? Checkpoint = null);
