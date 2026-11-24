using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpireReplay;

/// <summary>One indexed journal per run. A portable file is a ZIP of the same records.</summary>
public static class ReplayStore
{
    static readonly object Gate = new();
    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
    static readonly List<ReplayEvent> Pending = [];
    static RunInfo? active;
    static int room = -1;
    static string root = "";
    static Func<string> build = () => "";
    static Func<string> mods = () => "";
    static Action<Exception> report = _ => { };
    static System.Threading.Timer? timer;
    public static string LibraryDirectory => root;
    public static string ExportsDirectory => Path.Combine(root, "exports");

    internal static void Configure(string directory, Func<string> currentBuild, Func<string> currentMods, Action<Exception> onError)
    {
        lock (Gate)
        {
            FlushPending();
            active = null;
            room = -1;
            root = Path.GetFullPath(directory);
            Directory.CreateDirectory(root);
            build = currentBuild;
            mods = currentMods;
            report = onError;
            timer ??= new System.Threading.Timer(_ => { try { Flush(); } catch (Exception ex) { report(ex); } },
                null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }
    }

    static string RunPath(string id)
    {
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigit)) throw new InvalidDataException("Invalid run ID.");
        return Path.Combine(root, id);
    }

    static string RoomPath(string id, int index, string suffix)
    {
        if (index is < 0 or >= 4096) throw new ArgumentOutOfRangeException(nameof(index));
        return Path.Combine(RunPath(id), $"room-{index:D4}.{suffix}");
    }

    static void Atomic(string path, byte[] bytes)
    {
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }

    static void WriteIndex(RunInfo info) =>
        Atomic(Path.Combine(RunPath(info.Id), "run.json"), JsonSerializer.SerializeToUtf8Bytes(info, Json));

    static RunInfo ReadIndex(string id)
    {
        var info = JsonSerializer.Deserialize<RunInfo>(File.ReadAllBytes(Path.Combine(RunPath(id), "run.json")), Json)
            ?? throw new InvalidDataException("Missing run index.");
        ValidateIndex(info);
        if (id != info.Id) throw new InvalidDataException("Mismatched run ID.");
        return info;
    }

    static void ValidateIndex(RunInfo info)
    {
        if (info.Format != 1 || info.Id is null || info.Id.Length != 32 || !info.Id.All(char.IsAsciiHexDigit) ||
            info.Rooms is null || info.Rooms.Count > 4096 || info.Rooms.Where((r, i) => r == null || r.Index != i || r.Name == null).Any() ||
            info.Seed == null || info.Character == null || info.Build == null || info.Mods == null || info.Result == null)
            throw new InvalidDataException("Unsupported run index.");
    }

    public static void BeginRun(string runKey, string character, string version, string seed)
    {
        lock (Gate)
        {
            FlushPending();
            string id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(runKey)))[..32];
            var dir = RunPath(id);
            Directory.CreateDirectory(dir);
            active = File.Exists(Path.Combine(dir, "run.json")) ? ReadIndex(id) : new RunInfo
            { Id = id, Character = character, Build = version, Seed = seed, Mods = mods() };
            room = -1;
            WriteIndex(active);
        }
    }

    public static void BeginRoom(int floor, string name)
    {
        lock (Gate)
        {
            if (active == null) return;
            FlushPending();
            room = active.Rooms.Count;
            if (room >= 4096) { room = -1; return; }
            active.Rooms.Add(new RoomInfo(room, floor, name));
            WriteIndex(active);
        }
    }

    public static void Add(string kind, string label, int turn = 0, string card = "", int amount = 0, double? decisionSeconds = null, string actionKey = "", int? actionIndex = null)
    {
        lock (Gate)
        {
            if (active == null || room < 0) return;
            Pending.Add(new ReplayEvent(kind, label, turn, card, amount, decisionSeconds, actionKey, actionIndex));
            if (Pending.Count >= 512) FlushPending();
        }
    }

    static void FlushPending()
    {
        if (active == null || room < 0 || Pending.Count == 0) return;
        using (var writer = new StreamWriter(RoomPath(active.Id, room, "jsonl"), true, new UTF8Encoding(false)))
            foreach (var item in Pending) writer.WriteLine(JsonSerializer.Serialize(item, Json));
        Pending.Clear();
    }

    public static void Flush() { lock (Gate) FlushPending(); }
    public static void SaveCombat(byte[] bytes) => SaveRoomBytes("mcr", bytes);
    public static void SaveCheckpoint(byte[] bytes) => SaveRoomBytes("checkpoint", bytes);
    static void SaveRoomBytes(string suffix, byte[] bytes)
    {
        lock (Gate)
        {
            if (active == null || room < 0 || bytes.Length == 0) return;
            Atomic(RoomPath(active.Id, room, suffix), bytes);
            if ((suffix == "mcr" && !active.Rooms[room].HasCombat) || (suffix == "checkpoint" && !active.Rooms[room].HasCheckpoint))
            {
                if (suffix == "mcr") active.Rooms[room].HasCombat = true; else active.Rooms[room].HasCheckpoint = true;
                WriteIndex(active);
            }
        }
    }

    public static void EndRun(string result)
    {
        string id;
        lock (Gate)
        {
            if (active == null) return;
            FlushPending();
            active.Result = result;
            WriteIndex(active);
            id = active.Id;
            active = null;
            room = -1;
        }
        _ = Task.Run(() => { try { Export(id); } catch (Exception ex) { report(ex); } });
    }

    public static IReadOnlyList<RunInfo> ListRuns()
    {
        lock (Gate)
        {
            var runs = new List<RunInfo>();
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                string id = Path.GetFileName(dir);
                if (id.Length != 32 || !id.All(char.IsAsciiHexDigit)) continue;
                try { runs.Add(ReadIndex(id)); } catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { report(ex); }
            }
            return runs.OrderByDescending(r => r.StartedUtc).ToArray();
        }
    }

    public static string? CheckCompatibility(RunInfo run) => run.Build != build() ? $"Game build: {run.Build}"
        : run.Mods != mods() ? $"Mods: {run.Mods}" : null;

    public static List<ReplayEvent> LoadEvents(string id, int index)
    {
        lock (Gate)
        {
            if (active?.Id == id && room == index) FlushPending();
            string path = RoomPath(id, index, "jsonl");
            if (!File.Exists(path)) return [];
            return File.ReadLines(path).Where(l => l.Length != 0)
                .Select(l => JsonSerializer.Deserialize<ReplayEvent>(l, Json) ?? throw new InvalidDataException("Invalid event.")).ToList();
        }
    }

    public static RoomData LoadRoom(string id, int index)
    {
        lock (Gate)
        {
            var events = LoadEvents(id, index);
            string path = RoomPath(id, index, "mcr");
            string checkpoint = RoomPath(id, index, "checkpoint");
            return new RoomData(events, File.Exists(path) ? File.ReadAllBytes(path) : null, File.Exists(checkpoint) ? File.ReadAllBytes(checkpoint) : null);
        }
    }

    static IEnumerable<string> RunFiles(RunInfo info)
    {
        yield return "run.json";
        foreach (var r in info.Rooms)
            foreach (var ext in new[] { "jsonl", "mcr", "checkpoint" }) yield return $"room-{r.Index:D4}.{ext}";
    }

    public static string Export(string id)
    {
        RunInfo info;
        lock (Gate)
        {
            if (active?.Id == id)
            {
                FlushPending();
                return WriteArchive(ReadIndex(id));
            }
            info = ReadIndex(id);
        }
        return WriteArchive(info);
    }

    static string WriteArchive(RunInfo info)
    {
        Directory.CreateDirectory(ExportsDirectory);
        string path = Path.Combine(ExportsDirectory, info.Id + ".spireplay");
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
                foreach (var name in RunFiles(info))
                {
                    string file = Path.Combine(RunPath(info.Id), name);
                    if (File.Exists(file)) archive.CreateEntryFromFile(file, name, CompressionLevel.Fastest);
                }
            File.Move(temp, path, true);
            return path;
        }
        finally { File.Delete(temp); }
    }

    static bool SameRecords(string first, string second)
    {
        var names = Directory.GetFiles(first).Select(Path.GetFileName).Order().ToArray();
        if (!names.SequenceEqual(Directory.GetFiles(second).Select(Path.GetFileName).Order())) return false;
        foreach (var name in names)
        {
            using var left = File.OpenRead(Path.Combine(first, name!));
            using var right = File.OpenRead(Path.Combine(second, name!));
            if (left.Length != right.Length || !SHA256.HashData(left).SequenceEqual(SHA256.HashData(right))) return false;
        }
        return true;
    }

    public static string Import(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 12289 || archive.Entries.Any(e => e.Length > 64L * 1024 * 1024) ||
            archive.Entries.Sum(e => e.Length) > 256L * 1024 * 1024) throw new InvalidDataException("Replay exceeds the supported size.");
        var index = archive.GetEntry("run.json") ?? throw new InvalidDataException("Missing run index.");
        if (index.Length > 4 * 1024 * 1024) throw new InvalidDataException("Run index too large.");
        RunInfo info;
        using (var s = index.Open()) info = JsonSerializer.Deserialize<RunInfo>(s, Json) ?? throw new InvalidDataException();
        ValidateIndex(info);
        var allowed = RunFiles(info).ToHashSet(StringComparer.Ordinal);
        if (archive.Entries.Any(e => !allowed.Remove(e.FullName))) throw new InvalidDataException("Unexpected or duplicate entry.");
        lock (Gate)
        {
            var dest = RunPath(info.Id);
            if (active?.Id == info.Id) FlushPending();
            string staging = Path.Combine(root, ".import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var e in archive.Entries) e.ExtractToFile(Path.Combine(staging, e.FullName));
                // Verify the event streams before they are made available to the library.
                foreach (var file in Directory.EnumerateFiles(staging, "*.jsonl"))
                    foreach (var line in File.ReadLines(file))
                    {
                        var item = JsonSerializer.Deserialize<ReplayEvent>(line, Json);
                        if (item?.Kind == null || item.Label == null || item.Card == null) throw new InvalidDataException("Invalid event.");
                    }
                if (Directory.Exists(dest))
                {
                    if (SameRecords(dest, staging)) return info.Id;
                    // A later export of an unfinished run can have the same ID and different decisions.
                    // Keep both histories. Importing that exact archive again selects its existing copy.
                    using var source = File.OpenRead(path);
                    info.Id = Convert.ToHexStringLower(SHA256.HashData(source))[..32];
                    dest = RunPath(info.Id);
                    if (Directory.Exists(dest)) return info.Id;
                    Atomic(Path.Combine(staging, "run.json"), JsonSerializer.SerializeToUtf8Bytes(info, Json));
                }
                Directory.Move(staging, dest);
                return info.Id;
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
    }
}
