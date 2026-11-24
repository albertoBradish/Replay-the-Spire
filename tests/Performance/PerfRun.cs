using System.Diagnostics;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace ReplayPerf;

[ModInitializer(nameof(Install))]
public static class PerfRun
{
    private static readonly TaskCompletionSource<AssetLoadingSession> Common = new();
    private static readonly List<string> Decisions = [];
    private static bool started;

    public static void Install()
    {
        if (System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_PERF") is null) return;
        var harmony = new Harmony("replaythespire.perf");
        harmony.Patch(AccessTools.Method(typeof(AssetCache), nameof(AssetCache.CreateSession)),
            postfix: new HarmonyMethod(typeof(PerfRun), nameof(AssetSession)));
        harmony.Patch(AccessTools.Method(typeof(NMainMenu), nameof(NMainMenu._Ready)),
            postfix: new HarmonyMethod(typeof(PerfRun), nameof(MainMenu)));
    }

    private static void AssetSession(string name, AssetLoadingSession __result)
    {
        if (name == "Common") Common.TrySetResult(__result);
    }

    private static void MainMenu(NMainMenu __instance)
    {
        if (started) return;
        started = true;
        Callable.From(() => { _ = Run(__instance); }).CallDeferred();
    }

    private static async Task Until(SceneTree tree, Func<bool> ready, string step, int timeout = 35)
    {
        var start = Stopwatch.GetTimestamp();
        while (!ready())
        {
            if (Stopwatch.GetElapsedTime(start).TotalSeconds > timeout) throw new TimeoutException(step);
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
    }

    private static async Task Attack(Player who, ICombatState combat, SceneTree tree)
    {
        var card = who.PlayerCombatState!.Hand.Cards.First(c => c.TargetType == TargetType.AnyEnemy);
        var target = combat.HittableEnemies.First();
        Decisions.Add($"turn={combat.RoundNumber} card={card.Id} target={target.Monster?.Id}");
        var play = new PlayCardAction(card, target);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(play);
        await Until(tree, () => play.CompletionTask.IsCompleted, "attack");
    }

    private static async Task Run(NMainMenu menu)
    {
        var game = NGame.Instance!;
        var tree = game.GetTree();
        using var frames = new FrameProbe(tree);
        try
        {
            var arm = System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_PERF");
            if (!OS.GetUserDataDir().Contains("ReplayAcceptance-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Isolated QA profile required");
            var mods = ModManager.GetLoadedMods().Select(m => m.manifest?.id).Order().ToArray();
            if (mods.Contains("SpireReplay") != (arm == "on")) throw new InvalidOperationException("Wrong A/B arm");
            await Until(tree, () => menu.IsVisibleInTree(), "menu ready");
            await (await Common.Task.WaitAsync(TimeSpan.FromSeconds(45))).WaitForCompletion();
            await tree.ToSignal(tree.CreateTimer(1), SceneTreeTimer.SignalName.Timeout);
            SaveManager.Instance.Progress.EnableFtues = false;
            var hero = ModelDb.Character<Ironclad>();
            frames.Stage("start-run");
#if BETA
            var player = Player.CreateForNewRun(hero, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1);
            var run = await game.StartNewSingleplayerRun(player, true, ActModel.GetDefaultList(), [], "REPLAYPERF", GameMode.Standard, false);
#else
            var run = await game.StartNewSingleplayerRun(hero, true, ActModel.GetDefaultList(), [], "REPLAYPERF", GameMode.Standard);
#endif
            var point = run.Map.GetAllMapPoints().Where(m => m.PointType == MapPointType.Monster)
                .OrderBy(m => m.coord.row).ThenBy(m => m.coord.col).First();
            Decisions.Add($"map={point.coord}");
            frames.Stage("room-entry");
            await RunManager.Instance.EnterMapCoord(point.coord);
            var who = run.Players.Single();
            await Until(tree, () => who.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && who.PlayerCombatState.Hand.Cards.Count >= 4 && !RunManager.Instance.ActionExecutor.IsPaused, "hand");
            var combat = who.Creature.CombatState!;
            frames.Stage("idle");
            await tree.ToSignal(tree.CreateTimer(6), SceneTreeTimer.SignalName.Timeout);
            frames.Stage("combat");
            await Attack(who, combat, tree);
            var end = new EndPlayerTurnAction(who, combat.RoundNumber);
            Decisions.Add($"end={combat.RoundNumber}");
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
            await Until(tree, () => combat.RoundNumber >= 2 && who.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && who.PlayerCombatState.Hand.Cards.Count >= 4 && !RunManager.Instance.ActionExecutor.IsPaused, "turn 2");
            await Attack(who, combat, tree);
            await tree.ToSignal(tree.CreateTimer(1), SceneTreeTimer.SignalName.Timeout);
            var signature = new {
                turn = combat.RoundNumber, hp = who.Creature.CurrentHp, block = who.Creature.Block,
                energy = who.PlayerCombatState!.Energy, hand = who.PlayerCombatState.Hand.Cards.Select(c => c.Id.ToString()).ToArray(),
                enemies = combat.Enemies.Select(c => $"{c.Monster?.Id}:{c.CurrentHp}:{c.Block}").ToArray()
            };
            frames.Stage("save-and-exit");
            await game.ReturnToMainMenu();
            frames.Stop();
            var archive = Path.Combine(OS.GetUserDataDir(), "ReplayTheSpire");
            long bytes = Directory.Exists(archive) ? Directory.EnumerateFiles(archive, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) : 0;
            if (arm == "on" && bytes == 0) throw new InvalidOperationException("Recording produced no archive");
            var output = System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_PERF_OUTPUT")!;
            var size = tree.Root.Size;
            using var surface = tree.Root.GetTexture().GetImage();
            var render = surface.GetSize();
            var report = new { arm, mods, decisions = Decisions, signature, archiveBytes = bytes, stages = frames.Results,
                windowSize = new[] { size.X, size.Y }, renderSize = new[] { (int)render.X, (int)render.Y } };
            File.WriteAllText(output, JsonSerializer.Serialize(report));
            GD.Print("REPLAY_PERF_PASS");
        }
        catch (Exception error) { GD.PrintErr("REPLAY_PERF_FAILURE " + error); }
        finally { game.Quit(); }
    }
}

internal sealed class FrameProbe : IDisposable
{
    private readonly SceneTree tree;
    private readonly Process process = Process.GetCurrentProcess();
    private readonly List<double> samples = new(16384);
    private readonly List<long> memory = new(128);
    private string? stage;
    private long previous, since, lastMemory;
    private bool priorFrame;
    private int focused;
    internal Dictionary<string, object> Results { get; } = [];

    internal FrameProbe(SceneTree tree)
    {
        this.tree = tree;
        tree.ProcessFrame += OnFrame;
    }

    internal void Stage(string name)
    {
        Stop();
        stage = name;
        previous = since = Stopwatch.GetTimestamp();
        lastMemory = 0;
        priorFrame = false;
    }

    private void OnFrame()
    {
        if (stage == null) return;
        var now = Stopwatch.GetTimestamp();
        if (priorFrame && samples.Count < 16384)
        {
            samples.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
            if (tree.Root.HasFocus()) focused++;
        }
        priorFrame = true;
        previous = now;
        if (Stopwatch.GetElapsedTime(lastMemory, now).TotalSeconds >= 0.5 && memory.Count < 256)
        {
            process.Refresh();
            memory.Add(process.WorkingSet64);
            lastMemory = now;
        }
    }

    internal void Stop()
    {
        if (stage == null) return;
        var elapsed = Stopwatch.GetElapsedTime(since).TotalMilliseconds;
        var sorted = samples.Order().ToArray();
        double? P(double q) => sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * q) - 1];
        Results[stage] = new {
            frames = sorted.Length, wallMs = elapsed, p50Ms = P(.50), p95Ms = P(.95), p99Ms = P(.99), maxMs = P(1),
            focusRatio = sorted.Length == 0 ? 0 : (double)focused / sorted.Length,
            medianWorkingSetBytes = memory.Count == 0 ? 0 : memory.Order().ElementAt(memory.Count / 2),
            peakWorkingSetBytes = memory.Count == 0 ? 0 : memory.Max(), samplesMs = samples.ToArray()
        };
        samples.Clear();
        memory.Clear();
        focused = 0;
        stage = null;
    }

    public void Dispose()
    {
        tree.ProcessFrame -= OnFrame;
        process.Dispose();
    }
}
