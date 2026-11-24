using System.IO.Compression;
using SpireReplay;

string temp = Directory.CreateTempSubdirectory("ReplayStorageTest-").FullName;
void Configure(string name) => ReplayStore.Configure(Path.Combine(temp, name), () => "buildA", () => "modsA", e => throw e);
void Check(bool value, string what)
{
    if (!value) throw new Exception(what);
}
try
{
    Configure("record");
    ReplayStore.BeginRun("same-session", "铁甲战士", "buildA", "SEED");
    ReplayStore.BeginRoom(1, "第一层");
    ReplayStore.Add("play", "打击", 1, "strike", decisionSeconds: 1.25);
    ReplayStore.Add("damage", "造成伤害", 1, "strike", 6);
    ReplayStore.SaveCombat([2, 3, 5]);
    ReplayStore.SaveCheckpoint([7, 11]);
    ReplayStore.BeginRun("same-session", "铁甲战士", "buildA", "SEED");
    ReplayStore.BeginRoom(1, "另一条记录");
    ReplayStore.Add("event", "<选择 & 确定>", actionKey: "option", actionIndex: 0);
    var run = ReplayStore.ListRuns().Single();
    Check(run.Rooms.Count == 2 && run.Rooms[0].HasCombat && run.Rooms[0].HasCheckpoint, "revisit indices");
    Check(ReplayStore.CheckCompatibility(run) == null, "matching game");
    var data = ReplayStore.LoadRoom(run.Id, 0);
    Check(data.Events[0].DecisionSeconds == 1.25 && data.Events[1].Amount == 6, "event fields");
    Check(data.Combat!.SequenceEqual(new byte[] { 2, 3, 5 }) && data.Checkpoint![1] == 11, "native room payloads");
    var album = Gallery.Export(run.Id, -1);
    Check(File.ReadAllText(album).Contains("timeline-001.svg") &&
        File.ReadAllText(Path.Combine(Gallery.FrameDirectory(run.Id, 1), "timeline-001.svg")).Contains("&lt;"), "gallery escaping");
    File.AppendAllText(Path.Combine(ReplayStore.LibraryDirectory, run.Id, "room-0000.jsonl"), "{");
    Check(ReplayStore.LoadEvents(run.Id, 0).Count == 2, "recover final interrupted record");
    string file = ReplayStore.Export(run.Id);
    Configure("import");
    var id = ReplayStore.Import(file);
    Check(ReplayStore.Import(file) == id && ReplayStore.ListRuns().Count == 1, "idempotent import");
    Check(ReplayStore.LoadEvents(id, 1).Single().ActionIndex == 0, "structured choice");
    var bad = Path.Combine(temp, "malformed.spireplay");
    File.Copy(file, bad);
    using (var zip = ZipFile.Open(bad, ZipArchiveMode.Update)) zip.CreateEntry("../out");
    try { ReplayStore.Import(bad); throw new Exception("unexpected entry accepted"); } catch (InvalidDataException) { }
    Console.WriteLine("STORAGE_PASS");
}
finally { Directory.Delete(temp, recursive: true); }
