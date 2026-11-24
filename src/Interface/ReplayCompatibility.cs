using static SpireReplay.Interface.ReplayStyle;

namespace SpireReplay.Interface;

internal static class ReplayCompatibility
{
    internal static (string Summary, string Details)? Explain(RunInfo run, string currentBuild, string currentMods)
    {
        bool gameChanged = run.Build != currentBuild;
        bool modsChanged = run.Mods != currentMods;
        if (!gameChanged && !modsChanged) return null;
        var reasons = new List<string>();
        if (gameChanged)
            reasons.Add(T($"游戏版本不同 · 录制 {run.Build}，当前 {currentBuild}",
                $"Game build differs · recorded {run.Build}, installed {currentBuild}"));
        if (modsChanged)
        {
            var recorded = Modules(run.Mods);
            var installed = Modules(currentMods);
            int before = reasons.Count;
            foreach (string id in recorded.Keys.Union(installed.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (!recorded.TryGetValue(id, out var past))
                {
                    reasons.Add(T($"当前另有 {installed[id].Label}", $"{installed[id].Label} is installed only on this system"));
                }
                else if (!installed.TryGetValue(id, out var now))
                {
                    reasons.Add(T($"当前缺少 {past.Label}", $"Missing {past.Label}"));
                }
                else if (past.Version != now.Version)
                {
                    reasons.Add(T($"{id} 版本不同（录制 {past.Version}，当前 {now.Version}）",
                        $"{id} version differs (recorded {past.Version}, installed {now.Version})"));
                }
                else if (past.Fingerprint != now.Fingerprint)
                {
                    reasons.Add(T($"{past.Label} 构建不同", $"{past.Label} build differs"));
                }
            }
            if (before == reasons.Count) reasons.Add(T("模组加载顺序不同", "Mod load order differs"));
        }
        string hint = T("可查看时间线和卡牌统计", "Timeline and card totals are available");
        string primary = reasons[0];
        if (primary.Length > 100) primary = primary[..97] + "…";
        string more = reasons.Count > 1 ? T($"（另有 {reasons.Count - 1} 项）", $" (+{reasons.Count - 1} more)") : "";
        return ($"{primary}{more} · {hint}", string.Join("\n", reasons.Append(hint)));
    }

    private sealed record Module(string Id, string Version, string Fingerprint)
    {
        internal string Label => Version.Length == 0 ? Id : $"{Id} {Version}";
    }

    private static Dictionary<string, Module> Modules(string raw)
    {
        var result = new Dictionary<string, Module>(StringComparer.Ordinal);
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int split = line.IndexOf(' ');
            string label = split < 0 ? line : line[..split];
            int version = label.LastIndexOf('@');
            string id = version < 0 ? label : label[..version];
            result[id] = new Module(id, version < 0 ? "" : label[(version + 1)..], split < 0 ? "" : line[(split + 1)..]);
        }
        return result;
    }
}
