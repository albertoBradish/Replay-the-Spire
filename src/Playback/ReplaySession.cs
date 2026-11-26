using System.Diagnostics;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpireReplay.Playback;

internal sealed class ReplaySession(byte[] data, CombatReplay replay, int targetTurn, int targetDecision,
    IReadOnlyList<ReplayEvent>? roomEvents, IReadOnlyList<ReplayEvent>? combatTimeline, bool galleryCapture)
{
    private readonly SceneTree tree = NGame.Instance!.GetTree();
    private readonly double previousTimeScale = Engine.TimeScale;
    private readonly bool previouslyPaused = NGame.Instance!.GetTree().Paused;
    private readonly Dictionary<uint, ReplayChecksumData> expected =
        replay.checksumData.ToDictionary(check => check.checksumData.id);
    private ChecksumTracker? tracker;
    private string? mismatch;
    private int verifiedCount;
    private int lastTurn;
    private bool seeking = targetTurn > 0 || targetDecision >= 0;
    private bool impactSamplingEnded;
    private bool stepToNext;
    private int stepFrom;
    private int? nextPhotoDecision;
    private DecisionCaption? caption;
    private Task eventLayout = Task.CompletedTask;
    internal Task Playback { get; private set; } = Task.CompletedTask;
    private readonly BattleTimeline timeline = new(combatTimeline);
    private readonly Dictionary<int, double> decisionTimes = combatTimeline?
        .Where(e => e.Kind == "choice" && e.ActionKey == "combat" && e.ActionIndex.HasValue &&
            e.DecisionSeconds is double time && double.IsFinite(time) && time > 0)
        .GroupBy(e => e.ActionIndex!.Value).ToDictionary(g => g.Key, g => g.First().DecisionSeconds!.Value) ?? [];

    internal byte[] Data { get; } = data;
    internal CombatReplay Replay { get; } = replay;
    internal IReadOnlyList<ReplayEvent>? RoomEvents { get; } = roomEvents;
    internal IReadOnlyList<ReplayEvent>? CombatTimeline { get; } = combatTimeline;
    internal int Length => RoomEvents?.Count ?? Replay.events.Count + timeline.Before.Length + timeline.After.Length;
    private readonly int combatDecisions = replay.events.Count(e =>
        e.eventType == CombatReplayEventType.GameAction && e.action is not NetReadyToBeginEnemyTurnAction);
    internal int DecisionCount => RoomEvents?.Count ?? combatDecisions + timeline.Before.Length + timeline.After.Length;
    internal bool Ready { get; private set; }
    internal bool Closing { get; private set; }
    internal bool Finished { get; private set; }
    internal bool Verified { get; private set; }
    internal bool HasError { get; private set; }
    internal int NativeChoiceCount { get; private set; }
    internal int CaptionChoiceCount { get; private set; }
    internal bool NativeVictory { get; set; }
    internal bool Paused { get; private set; }
    internal bool Seeking => seeking;
    internal int Cursor { get; private set; }
    internal int Decision { get; private set; }
    internal int CombatDecision => RoomEvents == null ? Decision - timeline.Before.Length : 0;
    internal int Turn => RoomEvents != null ? 0 : CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? lastTurn;
    internal string Status { get; private set; } = "正在载入战斗";

    internal async Task StartAsync(bool warmCharacter, bool warmAct)
    {
        var run = RunState.FromSerializable(Replay.serializableRun);
        var player = run.Players.Single();
        var manager = RunManager.Instance;
        manager.SetUpReplay(run, Replay, player.NetId);
        manager.CombatStateSynchronizer.IsDisabled = true;
        manager.CombatReplayWriter.IsEnabled = false;

        long assetsStarted = Stopwatch.GetTimestamp();
        if (!warmCharacter) await PreloadManager.LoadRunAssets([player.Character]);
        if (!warmAct) await PreloadManager.LoadActAssets(run.Act);
        double preloadMilliseconds = Stopwatch.GetElapsedTime(assetsStarted).TotalMilliseconds;
        long restoreStarted = Stopwatch.GetTimestamp();
        CheckActive();
        manager.Launch();
        NAudioManager.Instance?.StopMusic();
        NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(run));
        await manager.GenerateMap();
        CheckActive();
        if (RoomEvents == null)
        {
            manager.PlayerChoiceSynchronizer.FastForwardChoiceIds(Replay.choiceIds);
            manager.RewardsSetSynchronizer.FastForwardRewardIds(Replay.rewardIds);
            manager.ActionQueueSet.FastForwardNextActionId(Replay.nextActionId);
            manager.ActionQueueSynchronizer.FastForwardHookId(Replay.nextHookId);
            tracker = manager.ChecksumTracker;
            tracker.LoadReplayChecksums(Replay.checksumData, Replay.nextChecksumId);
            tracker.ChecksumGenerated += Verify;
        }
        if (RoomEvents?.FirstOrDefault() is { Kind: "direct-event" } ending)
        {
            var architect = ModelDb.Event<TheArchitect>();
            if (ending.ActionKey != architect.Id.ToString() || ending.ActionIndex == null)
                throw new InvalidDataException("终局事件入口无效。");
            ReplaySandbox.RestoreEndingHistory(player.Character.Id, ending.Amount, ending.ActionIndex.Value);
            await manager.EnterRoom(new EventRoom(architect));
        }
        else
        {
            var previousRoom = AbstractRoom.FromSerializable(Replay.serializableRun.PreFinishedRoom, run);
            await manager.LoadIntoLatestMapCoord(previousRoom);
        }
        CheckActive();
        if (run.CurrentRoom is EventRoom)
        {
            await eventLayout;
            CheckActive();
            if (NEventRoom.Instance?.Layout is NAncientEventLayout ancient)
            {
                var last = AccessTools.Property(typeof(NAncientEventLayout), "IsDialogueOnLastLine");
                var advance = AccessTools.Method(typeof(NAncientEventLayout), "OnDialogueHitboxClicked");
                // Dialogue navigation reveals the buttons without choosing an option.
                while (last.GetValue(ancient) is false)
                    advance.Invoke(ancient, [null]);
            }
            await tree.ToSignal(tree.CreateTimer(1.55), SceneTreeTimer.SignalName.Timeout);
            CheckActive();
        }
        else if (RoomEvents != null && seeking)
        {
            await tree.ToSignal(tree.CreateTimer(0.7), SceneTreeTimer.SignalName.Timeout);
            CheckActive();
        }
        if (RoomEvents == null && timeline.Battle == null)
        {
            if (CombatManager.Instance.DebugOnlyGetState() == null)
                throw new InvalidDataException("该记录的入口未能恢复战斗房间。");
            await Until(() => !manager.ActionExecutor.IsPaused);
        }
        Ready = true;
        Status = seeking ? "正在定位" : "播放中";
        RefreshClock();
        if (galleryCapture)
            GD.Print($"[SpireReplay] Gallery room floor={run.TotalFloor} preload_ms={preloadMilliseconds:F0} restore_ms={Stopwatch.GetElapsedTime(restoreStarted).TotalMilliseconds:F0}");
        Playback = RoomEvents == null ? FeedAsync(run) : ShowRoomAsync(run, RoomEvents);
    }

    internal void RememberEventLayout(Task setup) => eventLayout = setup;

    private double SeekSpeed => galleryCapture && RoomEvents == null && !impactSamplingEnded &&
        CombatDecision > 0 && Decision + 1 == targetDecision ? 2 : 8;

    internal void EndImpactSampling()
    {
        if (!galleryCapture || impactSamplingEnded) return;
        impactSamplingEnded = true;
        RefreshClock();
    }

    internal void RefreshClock()
    {
        if (!Ready || Closing)
            return;
        tree.Paused = Paused;
        Engine.TimeScale = Paused ? 0 : previousTimeScale * (seeking ? SeekSpeed : ReplayPlayer.Speed);
    }

    internal void SetPaused(bool paused)
    {
        if (!Ready || Closing || Finished)
            return;
        stepToNext = false;
        nextPhotoDecision = null;
        seeking = false;
        Paused = paused;
        Status = paused ? "已暂停" : "播放中";
        RefreshClock();
    }

    internal void AdvanceToPhoto(int decision)
    {
        SetPaused(false);
        nextPhotoDecision = decision;
    }

    internal void Step()
    {
        if (!Ready || Closing || Finished)
            return;
        SetPaused(false);
        stepToNext = true;
        stepFrom = Decision;
    }

    internal void Cancel()
    {
        if (Closing)
            return;
        Closing = true;
        caption?.Close();
        caption = null;
        if (tracker != null)
            tracker.ChecksumGenerated -= Verify;
        if (Ready)
        {
            tree.Paused = previouslyPaused;
            Engine.TimeScale = previousTimeScale;
        }
    }

    private async Task FeedAsync(RunState run)
    {
        try
        {
            var queues = RunManager.Instance.ActionQueueSet;
            var executor = RunManager.Instance.ActionExecutor;
            if (timeline.Battle is { } nested)
            {
                string[] ids = nested.ActionKey.Split('|');
                if (ids.Length != 2 || run.CurrentRoom is not EventRoom ev || ev.ModelId.ToString() != ids[0])
                    throw new InvalidDataException("事件战斗的入口与记录不同。");
                await PlayChoicesAsync(run, timeline.Before);
                await Until(() => run.CurrentRoom is CombatRoom && CombatManager.Instance.IsInProgress && !executor.IsPaused);
                if (run.CurrentRoom is not CombatRoom restored || restored.Encounter.Id.ToString() != ids[1])
                    throw new InvalidDataException("事件战斗的遭遇与记录不同。");
                caption?.Close();
                caption = null;
            }
            var closed = ClosedDecisionBoundaries(Replay);
            var selections = new Queue<ReplayEvent>(CombatTimeline?.Where(e =>
                e.Kind == "choice" && e.Turn > 0 && e.ActionKey != "combat") ?? []);
            int endedTurn = 0;
            bool awaitingNextTurn = false;
            foreach (var entry in Replay.events)
            {
                CheckActive();
                bool decision = entry.eventType == CombatReplayEventType.GameAction &&
                    entry.action is not NetReadyToBeginEnemyTurnAction;
                if (decision && awaitingNextTurn)
                {
                    // Enemy-turn tasks run independently of the action queue. An empty
                    // executor can precede the next draw and the next player's turn ID.
                    await Until(() => run.Players[0].PlayerCombatState is { } player &&
                        player.TurnNumber > endedTurn && player.Phase == PlayerTurnPhase.Play);
                    awaitingNextTurn = false;
                }
                int offset = Cursor - timeline.Before.Length;
                if (decision && closed[offset])
                {
                    await Until(() => !executor.IsRunning);
                    lastTurn = Turn;
                    bool atTarget = seeking && (targetTurn > 0 && Turn >= targetTurn ||
                        targetDecision >= 0 && Decision + 1 >= targetDecision);
                    if (atTarget || stepToNext && Decision > stepFrom || nextPhotoDecision <= Decision + 1)
                        SetPaused(true);
                }
                await Until(() => true);
                if (decision && closed[offset] && decisionTimes.TryGetValue(Decision + 1 - timeline.Before.Length, out double seconds))
                    await WaitPresentationAsync(seconds, originalOnly: true);
                switch (entry.eventType)
                {
                    case CombatReplayEventType.GameAction:
                        await Until(() => !CombatManager.Instance.EndingPlayerTurnPhaseOne &&
                            !CombatManager.Instance.EndingPlayerTurnPhaseTwo);
                        var action = entry.action!.ToGameAction(run.Players[0]);
                        if (action.ActionType == GameActionType.CombatPlayPhaseOnly)
                            await Until(() => CombatManager.Instance.DebugOnlyGetState()?.CurrentSide == CombatSide.Player);
                        if (decision)
                        {
                            Decision++;
                            // Only the featured action needs enough render updates to show its impact.
                            if (galleryCapture && seeking && (Decision == targetDecision - 1 || Decision == targetDecision))
                                RefreshClock();
                        }
                        if (entry.action is NetEndPlayerTurnAction ending)
                        {
                            await Until(() => run.Players[0].PlayerCombatState?.TurnNumber == ending.turnNumber);
                            endedTurn = ending.turnNumber;
                        }
                        if (entry.action is NetReadyToBeginEnemyTurnAction)
                            awaitingNextTurn = true;
                        queues.EnqueueWithoutSynchronizing(action);
                        if (action is EndPlayerTurnAction or ReadyToBeginEnemyTurnAction)
                            await Until(() => !executor.IsRunning);
                        break;
                    case CombatReplayEventType.HookAction:
                        var hook = RunManager.Instance.ActionQueueSynchronizer.GetHookActionForId(
                            entry.hookId!.Value, entry.playerId!.Value, entry.gameActionType!.Value);
                        queues.EnqueueWithoutSynchronizing(hook);
                        break;
                    case CombatReplayEventType.PlayerChoice:
                        var result = entry.playerChoiceResult!.Value;
                        if (selections.TryPeek(out var selection) && IsRecordedSelection(selection, result.type))
                        {
                            selections.Dequeue();
                            if (selection.DecisionSeconds is double duration && double.IsFinite(duration) && duration > 0)
                                await WaitPresentationAsync(duration, originalOnly: true);
                        }
                        RunManager.Instance.PlayerChoiceSynchronizer.ReceiveReplayChoice(run.Players[0],
                            entry.choiceId!.Value, result);
                        break;
                    case CombatReplayEventType.ResumeAction:
                        queues.ResumeActionWithoutSynchronizing(entry.actionId!.Value);
                        break;
                    default:
                        throw new InvalidDataException("无法读取这个战斗动作。");
                }
                Cursor++;
            }
            await Until(() => !executor.IsRunning && verifiedCount == expected.Count);
            EndImpactSampling();
            Verified = verifiedCount > 0;
            if (tracker != null)
                tracker.ChecksumGenerated -= Verify;
            if (timeline.After.Length > 0)
            {
                if (timeline.After[0].Kind == "reward")
                    await WaitForRewardScreen();
                await PlayChoicesAsync(run, timeline.After);
            }
            Finished = true;
            Status = Verified ? $"已播完 · {verifiedCount} 个状态校验通过" : "已播完 · 无状态校验";
        }
        catch (OperationCanceledException) when (Closing) { return; }
        catch (Exception error)
        {
            if (Closing)
                return;
            Finished = true;
            HasError = true;
            Status = error.Message;
            GD.PushWarning($"Replay the Spire: {error}");
        }
        if (!Closing)
        {
            seeking = false;
            Paused = true;
            RefreshClock();
        }
    }

    private static bool IsRecordedSelection(ReplayEvent entry, PlayerChoiceType type) => type switch
    {
        PlayerChoiceType.Index => entry.ActionKey.StartsWith("index:", StringComparison.Ordinal),
        PlayerChoiceType.DeckCard => entry.ActionKey.StartsWith("deck:", StringComparison.Ordinal),
        PlayerChoiceType.CanonicalCard or PlayerChoiceType.MutableCard or PlayerChoiceType.CombatCard =>
            entry.ActionKey.StartsWith("cards:", StringComparison.Ordinal),
        _ => false
    };

    private async Task ShowRoomAsync(RunState run, IReadOnlyList<ReplayEvent> timeline)
    {
        try
        {
            await PlayChoicesAsync(run, timeline);
            Finished = true;
            Status = timeline.Count > 0 ? "房间记录已播完 · " + timeline[^1].Label : "房间入口";
        }
        catch (OperationCanceledException) when (Closing) { return; }
        catch (Exception error)
        {
            if (Closing)
                return;
            Finished = true;
            HasError = true;
            Status = error.Message;
            GD.PushWarning($"Replay the Spire: {error}");
        }
        if (!Closing)
        {
            seeking = false;
            Paused = true;
            RefreshClock();
        }
    }

    private async Task PlayChoicesAsync(RunState run, IReadOnlyList<ReplayEvent> timeline)
    {
        int shown = -1, prepared = -1;
        bool presentingGrid = false;
        async Task BeginChoice(int index)
        {
            if (prepared >= index) return;
            prepared = index;
            var entry = timeline[index];
            if (nextPhotoDecision <= Decision + 1 || seeking && targetDecision >= 0 && Decision + 1 >= targetDecision || stepToNext && Decision > stepFrom)
            {
                SetPaused(true);
                Status = $"第 {Decision + 1} 个节点 · {entry.Label}";
            }
            await Until(() => true);
            if (entry.DecisionSeconds is double seconds && double.IsFinite(seconds) && seconds > 0)
                await WaitPresentationAsync(seconds, originalOnly: true);
        }
        async Task ShowChoice(int index, bool animated)
        {
            var entry = timeline[index];
            if (animated) NativeChoiceCount++;
            else CaptionChoiceCount++;
            Status = (animated ? "选择 · " : "记录 · ") + entry.Label;
            caption ??= new DecisionCaption(tree);
            caption.Show(entry, animated);
            Decision++;
            Cursor++;
            shown = index;
            await WaitPresentationAsync(1.65);
        }
        async Task PresentGrid(int child, Action highlight, string titles)
        {
            presentingGrid = true;
            try
            {
                // The event option is still awaiting its nested picker. Expose the parent
                // and child as separate seek/step boundaries while that picker is visible.
                for (int previous = shown + 1; previous < child; previous++)
                {
                    await BeginChoice(previous);
                    await ShowChoice(previous, true);
                }
                await BeginChoice(child);
                await WaitPresentationAsync(1.2);
                highlight();
                caption ??= new DecisionCaption(tree);
                caption.Show(timeline[child] with { Label = titles.Length == 0 ? timeline[child].Label : titles }, true);
                await WaitPresentationAsync(1.25);
            }
            finally { presentingGrid = false; }
        }
        var driver = new RoomChoices(run, timeline, this.timeline.Battle != null, PresentGrid,
            ready => Until(ready), seconds => WaitPresentationAsync(seconds));
        for (int i = 0; i < timeline.Count; i++)
        {
            if (i <= shown) continue;
            var entry = timeline[i];
            await BeginChoice(i);
            if (entry.Kind == "event-resume" && run.CurrentRoom is CombatRoom context && !context.Encounter.ShouldGiveRewards)
                await Until(() => run.CurrentRoom is EventRoom && NEventRoom.Instance != null);
            if (entry.Kind == "event" && run.CurrentRoom is EventRoom { CanonicalEvent: TheArchitect } finale)
                await Until(() => finale.LocalMutableEvent.CurrentOptions.Count > 0);
            var applied = driver.ApplyAsync(i);
            await Until(() => applied.IsCompleted, () => presentingGrid);
            bool animated = await applied;
            if (i > shown) await ShowChoice(i, animated);
        }
    }

    private async Task WaitPresentationAsync(double seconds, bool originalOnly = false)
    {
        long previous = Stopwatch.GetTimestamp();
        while (seconds > 0 && !seeking && !stepToNext && (!originalOnly || ReplayPlayer.OriginalTiming))
        {
            CheckActive();
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            long now = Stopwatch.GetTimestamp();
            if (!Paused)
                seconds -= Stopwatch.GetElapsedTime(previous, now).TotalSeconds * ReplayPlayer.Speed;
            previous = now;
        }
        await Until(() => true);
    }

    private async Task WaitForRewardScreen()
    {
        // CombatWon schedules rewards after the monster's death animation. Give the
        // native screen time to open even during a seek or single-step.
        double remaining = 9;
        long previous = Stopwatch.GetTimestamp();
        while (!RewardChoices.Visible && remaining > 0)
        {
            CheckActive();
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            long now = Stopwatch.GetTimestamp();
            if (!Paused)
                remaining -= Stopwatch.GetElapsedTime(previous, now).TotalSeconds * (seeking ? SeekSpeed : ReplayPlayer.Speed);
            previous = now;
        }
    }

    // A later resume can belong to a choice opened by a card before the current
    // decision. Keep those inputs together; waiting between them would deadlock.
    private static bool[] ClosedDecisionBoundaries(CombatReplay replay)
    {
        var boundaries = new bool[replay.events.Count];
        var laterResumes = new Dictionary<uint, int>();
        for (int i = 0; i < replay.events.Count; i++)
            if (replay.events[i] is { eventType: CombatReplayEventType.ResumeAction, actionId: uint id })
                laterResumes[id] = i;
        uint nextId = replay.nextActionId;
        int closeAfter = -1;
        for (int i = 0; i < boundaries.Length; i++)
        {
            boundaries[i] = i > closeAfter;
            if (replay.events[i].eventType == CombatReplayEventType.PlayerChoice)
                continue;
            if (laterResumes.TryGetValue(nextId++, out int resume))
                closeAfter = Math.Max(closeAfter, resume);
        }
        return boundaries;
    }

    private void Verify(NetChecksumData actual, string context, NetFullCombatState state)
    {
        if (Closing || Finished || mismatch != null || tracker == null || !CombatManager.Instance.IsInProgress)
            return;
        if (!expected.TryGetValue(actual.id, out var original) ||
            tracker.GenerateChecksum(original.fullState) != actual.checksum)
            mismatch = $"第 {Turn} 回合、动作 {Cursor + 1} 的状态与记录不同（校验 {actual.id}）。";
        else
            verifiedCount++;
    }

    private void CheckActive()
    {
        if (Closing)
            throw new OperationCanceledException();
        if (mismatch != null)
            throw new InvalidDataException(mismatch);
    }

    private async Task Until(Func<bool> ready, Func<bool>? presenting = null)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            CheckActive();
            if (!Paused && ready())
                return;
            if (Paused || presenting?.Invoke() == true)
                clock.Restart();
            if (clock.Elapsed > TimeSpan.FromSeconds(35))
                throw new TimeoutException($"第 {Turn} 回合、动作 {Cursor + 1} 等待超时（已校验 {verifiedCount}/{expected.Count}）。");
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
    }
}
