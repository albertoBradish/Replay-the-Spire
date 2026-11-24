using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SpireReplay;

public static class Gallery
{
    public static string FrameDirectory(string runId, int roomIndex)
    {
        if (runId.Length != 32 || !runId.All(char.IsAsciiHexDigit) || roomIndex < 0) throw new ArgumentException();
        var dir = Path.Combine(ReplayStore.ExportsDirectory, "gallery-" + runId, $"room-{roomIndex:D4}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string Esc(string? s) => WebUtility.HtmlEncode(s ?? "");
    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Export(string runId, int roomIndex) => WriteIndex(runId, roomIndex, null, CancellationToken.None);

    internal static string WriteForum(string runId, string directory, CancellationToken token) =>
        WriteIndex(runId, -1, directory, token);

    private static string WriteIndex(string runId, int roomIndex, string? forumDirectory, CancellationToken token)
    {
        var run = ReplayStore.ListRuns().Single(x => x.Id == runId);
        var rooms = run.Rooms.Where(r => roomIndex < 0 || r.Index == roomIndex).ToList();
        if (rooms.Count == 0) throw new InvalidOperationException("No rooms selected.");
        var root = forumDirectory ?? Path.GetDirectoryName(FrameDirectory(run.Id, rooms[0].Index))!;
        var html = new StringBuilder("<!doctype html><html lang='zh-CN'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>回放尖塔 · 对局图集</title><style>body{max-width:960px;margin:32px auto;padding:0 20px;background:#142025;color:#f4ead4;font:16px/1.55 system-ui}img{display:block;max-width:100%;margin:12px 0;border-radius:8px}h1,h2{color:#ebc67f}a{color:inherit}</style><h1>回放尖塔 · 对局图集</h1>");
        html.Append("<p>").Append(Esc(run.Character)).Append(" · ").Append(Esc(run.Seed)).Append("</p>");
        var post = new StringBuilder().AppendLine($"【回放尖塔】{run.Character} · {run.Seed}");
        int postNumber = 0;
        foreach (var room in rooms)
        {
            token.ThrowIfCancellationRequested();
            string dir = forumDirectory == null ? FrameDirectory(run.Id, room.Index) : Path.Combine(root, $"room-{room.Index:D4}");
            Directory.CreateDirectory(dir);
            string rel = Path.GetFileName(dir) + "/";
            var events = ReplayStore.LoadEvents(run.Id, room.Index);
            html.Append("<section><h2>").Append(N(room.Floor)).Append("F · ").Append(Esc(room.Name)).Append("</h2>");
            postNumber++;
            post.AppendLine().AppendLine((forumDirectory == null ? "" : $"{postNumber} 楼 ｜ ") + $"第 {room.Floor} 层 · {room.Name}");
            int photoNumber = 0;
            foreach (var png in Directory.EnumerateFiles(dir, "*.png").Order(StringComparer.Ordinal).ToArray())
            {
                string file = Path.GetFileName(png);
                string pictures = rel;
                var match = Regex.Match(file, @"^auto-(?<action>\d+)-turn-(?<turn>\d+)|^t(?<turn>\d+)-e(?<action>\d+)");
                string caption = room.Name;
                if (match.Success)
                {
                    int turn = int.Parse(match.Groups["turn"].Value, CultureInfo.InvariantCulture);
                    int action = int.Parse(match.Groups["action"].Value, CultureInfo.InvariantCulture);
                    caption = $"第 {turn} 回合 · 动作 {action}";
                }
                var choice = Regex.Match(file, @"^choice-(\d+)");
                if (choice.Success && int.TryParse(choice.Groups[1].Value, out int choiceNumber) && choiceNumber > 0 && choiceNumber <= events.Count)
                    caption = events[choiceNumber - 1].Label;
                if (forumDirectory != null)
                {
                    pictures = "images/";
                    var flat = Path.Combine(root, "images");
                    Directory.CreateDirectory(flat);
                    file = $"{postNumber:D3}-F{room.Floor:D2}-{++photoNumber:D2}.png";
                    File.Move(png, Path.Combine(flat, file));
                }
                html.Append("<figure><img loading='lazy' alt='游戏画面' src='").Append(pictures)
                    .Append(Uri.EscapeDataString(file)).Append("'><figcaption>").Append(Esc(caption)).Append("</figcaption></figure>");
                post.AppendLine($"[图 {pictures}{file}] {caption}");
            }
            var highlights = forumDirectory == null ? events : Highlights(events);
            foreach (var ev in highlights.Where(e => e.Kind is "play" or "auto" or "reward" or "shop" or "camp" or "event" or "map" or "treasure" or "damage" or "block"))
                post.AppendLine((ev.Turn > 0 ? $"第 {ev.Turn} 回合 · " : "") + ev.Label +
                    (ev.Kind == "damage" ? $"，实际扣血 {ev.Amount}" : ev.Kind == "block" ? $"，获得格挡 {ev.Amount}" : ""));
            var lines = highlights.Where(x => x.Kind is not ("turn" or "draw")).ToList();
            if (lines.Count == 0) lines = highlights;
            const int perPage = 7;
            for (int start = 0; start < Math.Max(1, lines.Count); start += perPage)
            {
                var page = lines.Skip(start).Take(perPage).ToArray();
                string filename = $"timeline-{start / perPage + 1:D3}.svg";
                var svg = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' width='960' height='640' viewBox='0 0 960 640'><rect width='960' height='640' fill='#17282d'/><g font-family='Microsoft YaHei, sans-serif' fill='#f8efdd'><text x='50' y='62' font-size='28' fill='#e9be75'>回放尖塔 · Replay the Spire</text>");
                svg.Append("<text x='50' y='123' font-size='38'>").Append(Esc(Clip(room.Floor + "F · " + room.Name, 31))).Append("</text>");
                int row = 0;
                foreach (var ev in page)
                {
                    int y = 190 + 53 * row++;
                    string annot = ev.DecisionSeconds is { } sec ? $"{sec:0.0}s" : ev.Amount != 0 ? N(ev.Amount) : "";
                    svg.Append($"<text x='50' y='{y}' font-size='20' fill='#d2bb89'>").Append(ev.Turn > 0 ? "T" + N(ev.Turn) : "·").Append("</text>")
                       .Append($"<text x='118' y='{y}' font-size='23'>").Append(Esc(Clip(ev.Label, 29))).Append("</text>")
                       .Append($"<text x='900' y='{y}' text-anchor='end' font-size='21' fill='#90d3bd'>").Append(Esc(annot)).Append("</text>");
                }
                svg.Append("<text x='50' y='595' font-size='19' fill='#acbcb8'>").Append(Esc(Clip(run.Character + " · " + run.Seed, 40))).Append("</text></g></svg>");
                File.WriteAllText(Path.Combine(dir, filename), svg.ToString(), Encoding.UTF8);
                html.Append("<img loading='lazy' alt='对局时间线' src='").Append(rel).Append(filename).Append("'>");
            }
            html.Append("</section>");
        }
        string index = Path.Combine(root, roomIndex < 0 ? "index.html" : $"room-{roomIndex:D4}.html");
        File.WriteAllText(index, html.ToString(), Encoding.UTF8);
        File.WriteAllText(Path.ChangeExtension(index, ".txt"), post.ToString(), Encoding.UTF8);
        return index;
    }

    private static List<ReplayEvent> Highlights(List<ReplayEvent> events)
    {
        var choices = events.Where(e => e.Kind is "reward" or "shop" or "camp" or "event" or "map" or "treasure");
        var selected = choices.Take(3).Concat(choices.TakeLast(1)).ToHashSet();
        if (events.FirstOrDefault(e => e.Kind == "play") is { } opening) selected.Add(opening);
        if (events.Where(e => e.Kind == "damage").MaxBy(e => e.Amount) is { } hit) selected.Add(hit);
        if (events.Where(e => e.Kind == "block").MaxBy(e => e.Amount) is { } block) selected.Add(block);
        if (selected.Count == 0) return events.Take(6).ToList();
        return events.Where(selected.Contains).Distinct().Take(7).ToList();
    }

    static string Clip(string text, int count)
    {
        var graphemes = StringInfo.ParseCombiningCharacters(text);
        return graphemes.Length <= count ? text : text[..graphemes[count]] + "…";
    }
}
