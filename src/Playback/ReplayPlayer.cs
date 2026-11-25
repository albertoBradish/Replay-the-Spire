using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using System.Text.Json;

namespace SpireReplay.Playback;

public static class ReplayPlayer
{
    private static readonly SemaphoreSlim transition = new(1);
    private static ReplaySession? current;
    private static string lastStatus = "";
    private static double speed = 1;
    private static Task menuAssets = Task.CompletedTask;
    private static IReadOnlySet<string>? retainedRunAssets;
    private static bool quitting;

    public static bool IsActive => current != null;
    public static bool IsReady => current is { Ready: true, Closing: false };
    public static bool IsPaused => current?.Paused ?? false;
    public static bool IsFinished => current?.Finished ?? false;
    public static bool IsSeeking => current?.Seeking ?? false;
    public static bool IsVerified => current?.Verified ?? false;
    public static bool HasError => current?.HasError ?? false;
    public static int NativeChoiceCount => current?.NativeChoiceCount ?? 0;
    public static int CaptionChoiceCount => current?.CaptionChoiceCount ?? 0;
    public static bool NativeVictory => current?.NativeVictory ?? false;
    public static bool CanStep => IsReady && !IsFinished;
    public static int CurrentTurn => current?.Turn ?? 0;
    public static int CurrentEvent => current?.Cursor ?? 0;
    public static int EventCount => current?.Length ?? 0;
    public static bool IsRoomTimeline => current?.RoomEvents != null;
    public static int DecisionCount => current?.DecisionCount ?? 0;
    public static int CurrentDecision => current?.Decision ?? 0;
    internal static int CurrentCombatDecision => current?.CombatDecision ?? 0;
    public static double Speed => speed;
    public static bool OriginalTiming { get; set; }
    public static string Status => current?.Status ?? lastStatus;

    public static void Install(Harmony harmony)
    {
        ReplaySandbox.Install(harmony);
        ReplayDecoder.Install(harmony);
        RoomChoiceAnswers.Install(harmony);
        harmony.Patch(AccessTools.Method(typeof(AssetCache), nameof(AssetCache.CreateSession)),
            postfix: new HarmonyMethod(typeof(ReplayPlayer), nameof(AssetsStarted)));
        harmony.Patch(AccessTools.Method(typeof(AssetCache), nameof(AssetCache.UnloadAssets)),
            prefix: new HarmonyMethod(typeof(ReplayPlayer), nameof(KeepGalleryAssets)));
        harmony.Patch(AccessTools.Method(typeof(PreloadManager), nameof(PreloadManager.LoadCommonAndMainMenuAssets)),
            postfix: new HarmonyMethod(typeof(ReplayPlayer), nameof(WaitForMenuOnExit)));
        harmony.Patch(AccessTools.Method(typeof(NGame), nameof(NGame.Quit)),
            prefix: new HarmonyMethod(typeof(ReplayPlayer), nameof(Quitting)));
        harmony.Patch(AccessTools.Method(typeof(NEventRoom), "SetupLayout"),
            postfix: new HarmonyMethod(typeof(ReplayPlayer), nameof(EventLayoutStarted)));
        harmony.Patch(AccessTools.Method(typeof(RunManager), nameof(RunManager.OnEnded)),
            postfix: new HarmonyMethod(typeof(ReplayPlayer), nameof(Outcome)));
    }

    public static Task PlayAsync(byte[] combat, int turn = 0) => PlayAsync(combat, turn, null);

    public static Task PlayAsync(byte[] combat, int turn, IReadOnlyList<ReplayEvent>? timeline) =>
        StartAsync(combat, turn, -1, null, timeline);

    public static Task PlayDecisionAsync(byte[] combat, int decision) => PlayDecisionAsync(combat, decision, null);

    public static Task PlayDecisionAsync(byte[] combat, int decision, IReadOnlyList<ReplayEvent>? timeline) =>
        PlayDecisionAsync(combat, decision, timeline, galleryCapture: false);

    internal static Task PlayDecisionAsync(byte[] combat, int decision, IReadOnlyList<ReplayEvent>? timeline, bool galleryCapture) =>
        StartAsync(combat, 0, checked(Math.Max(1, decision) + new BattleTimeline(timeline).Before.Length), null, timeline, galleryCapture);

    public static Task PlayRoomAsync(byte[] checkpoint, IReadOnlyList<ReplayEvent> events, int decision = 0) =>
        PlayRoomAsync(checkpoint, events, decision, galleryCapture: false);

    internal static Task PlayRoomAsync(byte[] checkpoint, IReadOnlyList<ReplayEvent> events, int decision, bool galleryCapture) =>
        StartAsync(checkpoint, 0, decision > 0 ? decision : -1, events, galleryCapture: galleryCapture);

    public static Task SeekTurnAsync(int turn) =>
        current is { RoomEvents: null } session ? StartAsync(session.Data, Math.Max(1, turn), -1, null, session.CombatTimeline) : Task.CompletedTask;

    public static Task SeekDecisionAsync(int decision) =>
        current is { } session ? StartAsync(session.Data, 0, Math.Max(0, decision), session.RoomEvents, session.CombatTimeline) : Task.CompletedTask;

    public static void SetPaused(bool paused) => current?.SetPaused(paused);

    public static void SetSpeed(double multiplier)
    {
        if (!double.IsFinite(multiplier))
            return;
        speed = Math.Clamp(multiplier, 0.25, 8);
        current?.RefreshClock();
    }

    internal static void AdvanceToPhoto(int decision) => current?.AdvanceToPhoto(decision);
    internal static void EndImpactSampling() => current?.EndImpactSampling();

    public static void Step() => current?.Step();

    private static async Task StartAsync(byte[] data, int turn, int decision, IReadOnlyList<ReplayEvent>? roomEvents,
        IReadOnlyList<ReplayEvent>? combatTimeline = null, bool galleryCapture = false)
    {
        await transition.WaitAsync();
        try
        {
            var game = NGame.Instance ?? throw new InvalidOperationException("请在主菜单打开回放。");
            if (current == null && (game.MainMenu == null || RunManager.Instance.IsInProgress))
                throw new InvalidOperationException("请先返回主菜单。");
            CombatReplay replay;
            if (roomEvents == null)
                replay = ReplayDecoder.Read(data);
            else
            {
                if (data.Length is 0 or > 16 * 1024 * 1024)
                    throw new InvalidDataException("房间记录大小无效。");
                var save = JsonSerializer.Deserialize(data, JsonSerializationUtility.GetTypeInfo<SerializableRun>());
                if (save?.Players.Count != 1)
                    throw new InvalidDataException("需要单人房间记录。");
                replay = new CombatReplay { serializableRun = save };
            }
            if (current != null)
                await CloseAsync(galleryCapture);
            // Returning to the menu schedules this native preload without awaiting
            // its completion. Its worker must release shared assets before relaunch.
            await menuAssets;
            if (SaveManager.Instance.CurrentRunSaveTask is { } pendingSave)
                await pendingSave;
            var session = new ReplaySession(data, replay, turn, decision, roomEvents, combatTimeline, galleryCapture);
            current = session;
            try
            {
                ReplaySandbox.Enter();
                await session.StartAsync();
            }
            catch
            {
                await CloseAsync();
                throw;
            }
        }
        catch (Exception error)
        {
            lastStatus = error.Message;
            throw;
        }
        finally
        {
            transition.Release();
        }
    }

    public static async Task StopAsync()
    {
        current?.Cancel();
        await transition.WaitAsync();
        try
        {
            await CloseAsync();
        }
        finally
        {
            transition.Release();
        }
    }

    private static async Task CloseAsync(bool galleryCapture = false)
    {
        if (current is not { } session)
            return;
        session.Cancel();
        // The temporary menu between gallery rooms can keep the current character's
        // assets. The native scene teardown and the final return still run normally.
        retainedRunAssets = galleryCapture ? AssetSets.RunSet : null;
        try
        {
            if (RunManager.Instance.IsInProgress)
                await NGame.Instance!.ReturnToMainMenu();
        }
        finally
        {
            retainedRunAssets = null;
            // The normal menu transition calls CleanUp before constructing its profile buttons.
            // Recover here too if startup failed before the native run was installed.
            if (ReferenceEquals(current, session))
                OnRunCleanedUp();
        }
    }

    internal static void OnRunCleanedUp()
    {
        if (quitting || current is not { } session)
            return;
        session.Cancel();
        ReplaySandbox.Leave();
        lastStatus = session.Status;
        current = null;
    }

    private static void KeepGalleryAssets(AssetCache __instance, ref IEnumerable<string> __0)
    {
        if (ReferenceEquals(__instance, PreloadManager.Cache) && retainedRunAssets is { } keep)
            __0 = __0.Where(path => !keep.Contains(path));
    }

    private static void AssetsStarted(string name, AssetLoadingSession __result)
    {
        if (name == "Common")
            menuAssets = __result.WaitForCompletion();
    }

    private static void EventLayoutStarted(Task __result) => current?.RememberEventLayout(__result);

    private static void Outcome(bool isVictory)
    {
        if (current != null && isVictory) current.NativeVictory = true;
    }

    private static void WaitForMenuOnExit(ref Task __result)
    {
        if (current is { Closing: true })
            __result = FinishMenuPreloadAsync(__result);
    }

    private static async Task FinishMenuPreloadAsync(Task schedule)
    {
        await schedule;
        await menuAssets;
    }

    private static void Quitting(NGame __instance)
    {
        if (current == null || quitting)
            return;
        quitting = true;
        current.Cancel();
        // Keep quit's final settings, progress and profile writes in the viewer
        // store until all child scenes have left the tree.
        __instance.TreeExiting += () =>
        {
            quitting = false;
            OnRunCleanedUp();
        };
        if (RunManager.Instance.IsInProgress)
            RunManager.Instance.CleanUp(graceful: false);
    }
}
