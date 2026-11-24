using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Modding;

namespace SpireReplay;

public static class GameEnvironment
{
    public static string Build => ReleaseInfoManager.Instance.ReleaseInfo is { } r ? $"{r.Version}/{r.Commit}" : "unknown";
    // Keep load order: two mods may patch the same rule. The MVID identifies the loaded assembly build.
    public static string Mods => string.Join("\n", ModManager.GetLoadedMods()
        .Where(m => m.manifest != null && m.manifest.id != "SpireReplay")
        .Select(m => $"{m.manifest!.id}@{m.manifest.version} {string.Join(",", m.assemblies.Select(a => a.ManifestModule.ModuleVersionId))}"));
}

[ModInitializer(nameof(Initialize))]
public static class Bootstrap
{
    public static void Initialize()
    {
        ReplayStore.Configure(Path.Combine(OS.GetUserDataDir(), "ReplayTheSpire"),
            () => GameEnvironment.Build, () => GameEnvironment.Mods, ex => GD.PushWarning($"[SpireReplay] {ex.Message}"));
        var harmony = new Harmony("io.github.albertobradish.replaythespire");
        Playback.ReplayPlayer.Install(harmony);
        Capture.CaptureHooks.Install(harmony);
        Interface.ReplayUi.Install(harmony);
        GD.Print("[SpireReplay] Ready");
    }
}
