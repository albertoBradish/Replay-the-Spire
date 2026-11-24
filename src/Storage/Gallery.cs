using System.Globalization;
using System.Net;
using System.Text;

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

    public static string Export(string runId, int roomIndex)
    {
        var run = ReplayStore.ListRuns().Single(x => x.Id == runId);
        var rooms = run.Rooms.Where(r => roomIndex < 0 || r.Index == roomIndex).ToList();
        if (rooms.Count == 0) throw new InvalidOperationException("No rooms selected.");
        var root = Path.GetDirectoryName(FrameDirectory(run.Id, rooms[0].Index))!;
        var html = new StringBuilder("<!doctype html><html lang='zh-CN'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>回放尖塔 · 对局图集</title><style>body{max-width:960px;margin:32px auto;padding:0 20px;background:#142025;color:#f4ead4;font:16px/1.55 system-ui}img{display:block;max-width:100%;margin:12px 0;border-radius:8px}h1,h2{color:#ebc67f}a{color:inherit}</style><h1>回放尖塔 · 对局图集</h1>");
        html.Append("<p>").Append(Esc(run.Character)).Append(" · ").Append(Esc(run.Seed)).Append("</p>");
        foreach (var room in rooms)
        {
            string dir = FrameDirectory(run.Id, room.Index);
            string rel = Path.GetFileName(dir) + "/";
            var events = ReplayStore.LoadEvents(run.Id, room.Index);
            html.Append("<section><h2>").Append(N(room.Floor)).Append("F · ").Append(Esc(room.Name)).Append("</h2>");
            foreach (var png in Directory.EnumerateFiles(dir, "*.png").Order(StringComparer.Ordinal))
                html.Append("<img loading='lazy' alt='游戏画面' src='").Append(rel).Append(Uri.EscapeDataString(Path.GetFileName(png))).Append("'>");
            var lines = events.Where(x => x.Kind is not ("turn" or "draw")).ToList();
            if (lines.Count == 0) lines = events;
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
        return index;
    }

    static string Clip(string text, int count)
    {
        var graphemes = StringInfo.ParseCombiningCharacters(text);
        return graphemes.Length <= count ? text : text[..graphemes[count]] + "…";
    }
}
