using HarmonyLib;
using MegaCrit.Sts2.Core.Leaderboard;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Saves.Test;

namespace SpireReplay.Playback;

internal static class ReplaySandbox
{
    private static SaveManager? isolated;

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.PropertyGetter(typeof(SaveManager), nameof(SaveManager.Instance)),
            prefix: new HarmonyMethod(typeof(ReplaySandbox), nameof(UseViewerProfile)));
        harmony.Patch(AccessTools.Method(typeof(RunManager), "InitializeShared"),
            prefix: new HarmonyMethod(typeof(ReplaySandbox), nameof(DisableRunSaving)));
        harmony.Patch(AccessTools.Method(typeof(RunManager), nameof(RunManager.WriteReplay)),
            prefix: new HarmonyMethod(typeof(ReplaySandbox), nameof(AllowProfileWrites)));
        harmony.Patch(AccessTools.Method(typeof(AchievementsUtil), nameof(AchievementsUtil.Unlock)),
            prefix: new HarmonyMethod(typeof(ReplaySandbox), nameof(AllowProfileWrites)));
        harmony.Patch(AccessTools.Method(typeof(LeaderboardManager), nameof(LeaderboardManager.UploadLocalScore)),
            prefix: new HarmonyMethod(typeof(ReplaySandbox), nameof(AllowScoreUpload)));
        harmony.Patch(AccessTools.Method(typeof(RunManager), nameof(RunManager.CleanUp)),
            postfix: new HarmonyMethod(typeof(ReplayPlayer), nameof(ReplayPlayer.OnRunCleanedUp)));
    }

    internal static void Enter()
    {
        var original = SaveManager.Instance;
        var profile = original.CurrentProfileId;
        var memory = new MockGodotFileIo("replay-memory") { getCurrentTime = () => DateTimeOffset.UtcNow };
        memory.WriteFile(SettingsSaveManager.settingsSaveFileName, SaveManager.ToJson(original.SettingsSave));
        memory.WriteFile(PrefsSaveManager.GetPrefsPath(profile), SaveManager.ToJson(original.PrefsSave));
        var progress = original.Progress.ToSerializable();
        progress.EnableFtues = false;
        progress.SchemaVersion = original.GetLatestSchemaVersion<SerializableProgress>();
        memory.WriteFile(ProgressSaveManager.GetProgressPathForProfile(profile), SaveManager.ToJson(progress));
        var viewer = new SaveManager(memory, forceSynchronous: true);
        viewer.InitProfileId();
        if (viewer.CurrentProfileId != profile)
            viewer.SwitchProfileId(profile);
        viewer.InitSettingsData();
        viewer.InitPrefsData();
        viewer.InitProgressData();
        isolated = viewer;
    }

    internal static void Leave() => isolated = null;

    internal static void RestoreEndingHistory(ModelId hero, int totalWins, int heroWins)
    {
        if (isolated == null || heroWins < 0 || totalWins < heroWins)
            throw new InvalidDataException("终局记录的胜场数无效。");
        // Only these two counters determine the Architect's available dialogue.
        // The backing state is the in-memory viewer profile.
        var progress = isolated.Progress;
        foreach (var entry in progress.CharacterStats.Values)
            entry.TotalWins = 0;
        progress.GetOrCreateCharacterStats(hero).TotalWins = heroWins;
        var other = ModelDb.Character<Ironclad>().Id == hero ? ModelDb.Character<Silent>().Id : ModelDb.Character<Ironclad>().Id;
        progress.GetOrCreateCharacterStats(other).TotalWins = totalWins - heroWins;
    }

    private static bool UseViewerProfile(ref SaveManager __result)
    {
        if (isolated == null)
            return true;
        __result = isolated;
        return false;
    }

    private static void DisableRunSaving(ref bool shouldSave)
    {
        if (isolated != null)
            shouldSave = false;
    }

    private static bool AllowProfileWrites() => isolated == null;

    private static bool AllowScoreUpload(ref Task __result)
    {
        if (isolated == null)
            return true;
        __result = Task.CompletedTask;
        return false;
    }
}
