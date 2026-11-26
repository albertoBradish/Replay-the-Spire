using Godot;
using MegaCrit.Sts2.Core.Assets;
using HarmonyLib;
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
using SpireReplay;
using SpireReplay.Playback;

namespace ReplayHarness;

[ModInitializer(nameof(Install))]
public static class NativeRun
{
    static bool started;
    static readonly TaskCompletionSource<AssetLoadingSession> commonAssets = new();
    static string evidence = "";
    public static void Install()
    {
        if (System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST") is null) return;
        var harness = new Harmony("replaythespire.test");
        if (System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TRACE_POOL") == "1") PoolTrace.Install(harness);
        harness.Patch(AccessTools.Method(typeof(AssetCache), nameof(AssetCache.CreateSession)), postfix: new HarmonyMethod(typeof(NativeRun), nameof(AssetSession)));
        harness.Patch(AccessTools.Method(typeof(NMainMenu), nameof(NMainMenu._Ready)),
            postfix: new HarmonyMethod(typeof(NativeRun), nameof(Menu)));
    }
    static void AssetSession(string name, AssetLoadingSession __result)
    {
        if (name == "Common") commonAssets.TrySetResult(__result);
    }
    static void Menu(NMainMenu __instance)
    {
        if (started) return;
        started = true;
        Callable.From(() => { _ = Execute(__instance); }).CallDeferred();
    }
    static async Task Wait(Func<bool> condition, string label, double timeout = 25)
    {
        var tree = NGame.Instance!.GetTree();
        var limit = Time.GetTicksMsec() + timeout * 1000;
        while (!condition())
        {
            if (Time.GetTicksMsec() > limit) throw new TimeoutException(label + " -- " + ReplayPlayer.Status);
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
    }
    static async Task Settle(double seconds)
    {
        var tree = NGame.Instance!.GetTree();
        await tree.ToSignal(tree.CreateTimer(seconds, true, false, true), SceneTreeTimer.SignalName.Timeout);
    }
    static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name + " " + ReplayPlayer.Status);
        GD.Print("[ReplayHarness] " + name);
    }
    static async Task Execute(NMainMenu menu)
    {
        var game = NGame.Instance!;
        try
        {
            evidence = System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST")!;
            Check(OS.GetUserDataDir().Contains("ReplayAcceptance", StringComparison.OrdinalIgnoreCase), "isolated profile");
            await Wait(() => menu.IsVisibleInTree() && menu.HasNode("ReplayTheSpireEntry"), "menu entry");
            var common = await commonAssets.Task.WaitAsync(TimeSpan.FromSeconds(45));
            await common.WaitForCompletion();
            await Settle(1);
            using (var image = game.GetTree().Root.GetTexture().GetImage())
                GD.Print($"[ReplayHarness] window={game.GetTree().Root.Size} texture={game.GetTree().Root.GetTexture().GetSize()} pixels={image.GetSize()}");
            string phase = System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_PHASE") ?? "record";
            if (phase == "control") GD.Print("REPLAY_CONTROL_PASS");
            else if (phase == "damage-record") await RewardDamageAcceptance.RunAsync(game);
            else if (phase == "reward-record") await JourneyAcceptance.RewardRecordAsync(game);
            else if (phase == "reward-watch") await JourneyAcceptance.RewardWatchAsync(game);
            else if (phase == "journey-record") await JourneyAcceptance.RecordAsync(game);
            else if (phase == "journey-watch") await JourneyAcceptance.WatchAsync(game);
            else if (phase == "event-battle-record") await EventBattleAcceptance.RecordAsync(game);
            else if (phase == "event-battle-watch") await EventBattleAcceptance.WatchAsync();
            else if (phase == "event-grid-record") await EventGridAcceptance.RecordAsync(game);
            else if (phase == "event-grid-watch") await EventGridAcceptance.WatchAsync();
            else if (phase == "merchant-record") await CourierAcceptance.RecordAsync(game, merchant: true);
            else if (phase == "merchant-watch") await CourierAcceptance.WatchMerchantAsync();
            else if (phase == "courier-record") await CourierAcceptance.RecordAsync(game);
            else if (phase == "courier-watch") await CourierAcceptance.WatchAsync();
            else if (phase == "crystal-record") await CrystalSphereAcceptance.RecordAsync(game);
            else if (phase == "crystal-watch") await CrystalSphereAcceptance.WatchAsync();
            else if (phase == "sample-album")
            {
                string id = ReplayStore.Import(System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_SAMPLE")!);
                var run = ReplayStore.ListRuns().Single(r => r.Id == id);
                // Run this file without changing its original mod fingerprint in storage.
                // The test-only assembly adds no content to the original game simulation.
                var type = typeof(ReplayPlayer).Assembly.GetType("SpireReplay.Interface.ReplayToolbar", true)!;
                int? sampleRoom = int.TryParse(System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_SAMPLE_ROOM"),out int requested) ? requested : null;
                var target = sampleRoom.HasValue ? run.Rooms.Single(r => r.Index == sampleRoom) : run.Rooms.First();
                string method = sampleRoom.HasValue ? "CaptureAlbumAsync" : "CaptureForumAsync";
                var toolbar = Activator.CreateInstance(type, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    null, [game.GetTree(),run,target,false], null)!;
                AccessTools.Method(type,"Show").Invoke(toolbar,null);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var index = await (Task<string>)AccessTools.Method(type,method).Invoke(toolbar,null)!;
                    long first = timer.ElapsedMilliseconds;
                    timer.Restart();
                    var reused = await (Task<string>)AccessTools.Method(type,method).Invoke(toolbar,null)!;
                    if (reused != index) throw new InvalidDataException("Unchanged recording was rendered twice.");
                    GD.Print($"REPLAY_SAMPLE_ALBUM_PASS ms={first} repeatMs={timer.ElapsedMilliseconds} index={index}");
                }
                finally
                {
                    if (ReplayPlayer.IsActive) await ReplayPlayer.StopAsync();
                    AccessTools.Method(type,"Remove").Invoke(toolbar,null);
                }
            }
            else if (phase == "forum-album") await UiAcceptance.ForumAsync();
            else if (phase is "campaign-album" or "campaign-film")
            {
                var run = ReplayStore.ListRuns().Single(r => r.Seed == "CAMPAIGNREPLAY");
                if (phase == "campaign-album") await UiAcceptance.ForumAsync(run.Id);
                else
                {
                    using var route = AudioTestOutput.Open();
                    await VideoAcceptance.ExportAsync(run.Id, run.Rooms.Last(r => r.HasCombat).Index, wholeRun: true);
                }
                GD.Print("REPLAY_" + phase.Replace('-', '_').ToUpperInvariant() + "_PASS");
            }
            else if (phase == "transfer") await TransferAcceptance.RunAsync(game);
            else if (phase == "foreign-transfer") await TransferAcceptance.RunAsync(game, false);
            else if (phase == "resume-record") await ResumeAcceptance.ContinueAsync(menu, game);
            else if (phase == "resume-watch") await ResumeAcceptance.WatchAsync();
            else if (phase == "facilities-record") await FacilityAcceptance.RecordAsync(game);
            else if (phase == "facilities-watch") await FacilityAcceptance.WatchAsync();
            else if (phase == "choices-record") await CombatChoicesAcceptance.RecordAsync(game);
            else if (phase == "choices-watch") await CombatChoicesAcceptance.WatchAsync();
            else if (phase == "timing-watch") await TimingAcceptance.WatchAsync();
            else if (phase == "timing") await TimingAcceptance.RunAsync(game);
            else if (phase == "modded-record") await ModdedAcceptance.RecordAsync(game);
            else if (phase == "modded-watch") await ModdedAcceptance.WatchAsync();
            else if (phase == "characters-record") await CharactersAcceptance.RecordAsync(game);
            else if (phase == "characters-watch") await CharactersAcceptance.WatchAsync();
            else if (phase == "campaign-record") await CampaignAcceptance.RecordAsync(game);
            else if (phase == "ordinary-record") await OrdinaryAcceptance.RecordAsync(game);
            else if (phase == "ordinary-watch") await OrdinaryAcceptance.WatchAsync();
            else if (phase == "campaign-watch") await CampaignAcceptance.WatchAsync();
            else if (phase == "video-muted")
            {
                var session = ReplayStore.ListRuns().First(r => r.Seed == "REPLAYPROOF");
                using var route = AudioTestOutput.Open(audible: false);
                await VideoAcceptance.MutedAsync(session.Id, session.Rooms.Last(r => r.HasCombat).Index);
                GD.Print("REPLAY_VIDEO_MUTED_PASS");
            }
            else if (phase == "video-muted-mix") await MutedExportAcceptance.RunAsync(game);
            else if (phase == "video-sync") await SyncAcceptance.RunAsync(game);
            else if (phase.StartsWith("video-", StringComparison.Ordinal))
            {
                var recorded = ReplayStore.ListRuns().First(r => r.Seed == (phase == "video-run" ? "JOURNEYREPLAY" : "REPLAYPROOF"));
                var battle = recorded.Rooms.Last(r => r.HasCombat);
                using (AudioTestOutput.Open())
                {
                    if (phase == "video-hd") await VideoAcceptance.HdAsync(recorded.Id, battle.Index);
                    else await VideoAcceptance.ExportAsync(recorded.Id, battle.Index, wholeRun: phase == "video-run", testCancel: phase == "video-cancel");
                }
                GD.Print("REPLAY_" + phase.Replace('-', '_').ToUpperInvariant() + "_PASS");
            }
            else if (phase == "record")
            {
                SaveManager.Instance.Progress.EnableFtues = false;
                var hero = ModelDb.Character<Ironclad>();
#if BETA
                var player = Player.CreateForNewRun(hero, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1);
                var run = await game.StartNewSingleplayerRun(player, true, ActModel.GetDefaultList(), [], "REPLAYPROOF", GameMode.Standard, false);
#else
                var run = await game.StartNewSingleplayerRun(hero, true, ActModel.GetDefaultList(), [], "REPLAYPROOF", GameMode.Standard);
#endif
                var point = run.Map.GetAllMapPoints().Where(m => m.PointType == MapPointType.Monster).OrderBy(m => m.coord.row).ThenBy(m => m.coord.col).First();
                await RunManager.Instance.EnterMapCoord(point.coord);
                var who = run.Players.Single();
                await Wait(() => (who.PlayerCombatState?.Hand.Cards.Count ?? 0) >= 4 && !RunManager.Instance.ActionExecutor.IsPaused, "opening hand");
                var combat = who.Creature.CombatState!;
                var card = who.PlayerCombatState!.Hand.Cards.First(c => c.TargetType == TargetType.AnyEnemy);
                var act = new PlayCardAction(card, combat.HittableEnemies.First());
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(act);
                await Wait(() => act.CompletionTask.IsCompleted, "attack resolved");
                var end = new EndPlayerTurnAction(who, combat.RoundNumber);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                await Wait(() => combat.RoundNumber >= 2 && !RunManager.Instance.ActionExecutor.IsPaused && who.PlayerCombatState.Hand.Cards.Count >= 4, "turn 2");
                var next = who.PlayerCombatState.Hand.Cards.First(c => c.TargetType == TargetType.AnyEnemy);
                var again = new PlayCardAction(next, combat.HittableEnemies.First());
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(again);
                await Wait(() => again.CompletionTask.IsCompleted, "second attack resolved");
                await Settle(0.5);
                await game.ReturnToMainMenu();
                ReplayStore.Flush();
                var recorded = ReplayStore.ListRuns().First(r => r.Seed == "REPLAYPROOF");
                var battle = recorded.Rooms.Last(r => r.HasCombat);
                var room = ReplayStore.LoadRoom(recorded.Id, battle.Index);
                Check(room.Combat is {Length: > 0} && room.Events.Count(e => e.Kind == "play") >= 2 && room.Events.Any(e => e.Kind == "damage"), "native combat and direct stats");
                Check(File.Exists(ReplayStore.Export(recorded.Id)), "portable file");
                GD.Print("REPLAY_RECORD_PASS");
            }
            else
            {
                var recorded = ReplayStore.ListRuns().First(r => r.Seed == "REPLAYPROOF");
                var battle = recorded.Rooms.Last(r => r.HasCombat);
                Check(ReplayStore.CheckCompatibility(recorded) == null, "matching environment");
                var room = ReplayStore.LoadRoom(recorded.Id, battle.Index);
                menu.GetNode<Button>("ReplayTheSpireEntry").EmitSignal(Button.SignalName.Pressed);
                await Settle(0.4);
                game.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(evidence, "library.png"));
                await ReplayPlayer.PlayAsync(room.Combat!);
                ReplayPlayer.SetPaused(true);
                await Settle(0.15);
                Check(ReplayPlayer.IsPaused, "pause");
                ReplayPlayer.Step();
                await Settle(0.4);
                ReplayPlayer.SetSpeed(2);
                ReplayPlayer.SetPaused(false);
                await Wait(() => ReplayPlayer.IsFinished, "completed initial replay", 60);
                Check(ReplayPlayer.IsVerified, "all original native checksums");
                await ReplayPlayer.SeekTurnAsync(1);
                await Wait(() => ReplayPlayer.IsPaused || ReplayPlayer.IsFinished, "seek turn one");
                ReplayPlayer.SetPaused(false);
                await Wait(() => ReplayPlayer.IsFinished, "replayed after seek", 60);
                Check(ReplayPlayer.IsVerified, "verified after seek");
                await ReplayPlayer.StopAsync();
                Check(!ReplayPlayer.IsActive && !RunManager.Instance.IsInProgress, "leave playback");
                Check(File.Exists(Gallery.Export(recorded.Id, battle.Index)), "export gallery");
                await UiAcceptance.AlbumAsync(recorded.Id, battle.Index);
                GD.Print("REPLAY_WATCH_PASS");
            }
            GD.Print("REPLAY_PHASE_PASS phase=" + phase.Replace('-', '_'));
        }
        catch (Exception e) { GD.PrintErr("REPLAY_TEST_FAILURE " + e); }
        finally { await Settle(2); game.Quit(); }
    }
}
