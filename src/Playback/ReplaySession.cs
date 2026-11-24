using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpireReplay.Playback;

internal sealed class ReplaySession(byte[] data, CombatReplay replay, int targetTurn, int targetDecision,
    IReadOnlyList<ReplayEvent>? roomEvents)
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
    private bool stepToNext;
    private int stepFrom;

    internal byte[] Data { get; } = data;
    internal CombatReplay Replay { get; } = replay;
    internal IReadOnlyList<ReplayEvent>? RoomEvents { get; } = roomEvents;
    internal int Length => RoomEvents?.Count ?? Replay.events.Count;
    internal int DecisionCount { get; } = roomEvents?.Count ?? replay.events.Count(e =>
        e.eventType == CombatReplayEventType.GameAction && e.action is not NetReadyToBeginEnemyTurnAction);
    internal bool Ready { get; private set; }
    internal bool Closing { get; private set; }
    internal bool Finished { get; private set; }
    internal bool Verified { get; private set; }
    internal bool HasError { get; private set; }
    internal bool Paused { get; private set; }
    internal bool Seeking => seeking;
    internal int Cursor { get; private set; }
    internal int Decision { get; private set; }
    internal int Turn => RoomEvents != null ? 0 : CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? lastTurn;
    internal string Status { get; private set; } = "正在载入战斗";

    internal async Task StartAsync()
    {
        var run = RunState.FromSerializable(Replay.serializableRun);
        var player = run.Players.Single();
        var manager = RunManager.Instance;
        manager.SetUpReplay(run, Replay, player.NetId);
        manager.CombatStateSynchronizer.IsDisabled = true;
        manager.CombatReplayWriter.IsEnabled = false;

        await PreloadManager.LoadRunAssets([player.Character]);
        await PreloadManager.LoadActAssets(run.Act);
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
        var previousRoom = AbstractRoom.FromSerializable(Replay.serializableRun.PreFinishedRoom, run);
        await manager.LoadIntoLatestMapCoord(previousRoom);
        CheckActive();
        if (RoomEvents == null)
        {
            if (CombatManager.Instance.DebugOnlyGetState() == null)
                throw new InvalidDataException("该记录的入口未能恢复战斗房间。");
            await Until(() => !manager.ActionExecutor.IsPaused);
        }
        Ready = true;
        Status = seeking ? "正在定位" : "播放中";
        RefreshClock();
        _ = RoomEvents == null ? FeedAsync(run) : ShowRoomAsync(run, RoomEvents);
    }

    internal void RefreshClock()
    {
        if (!Ready || Closing)
            return;
        tree.Paused = Paused;
        Engine.TimeScale = Paused ? 0 : previousTimeScale * (seeking ? 8 : ReplayPlayer.Speed);
    }

    internal void SetPaused(bool paused)
    {
        if (!Ready || Closing || Finished)
            return;
        stepToNext = false;
        seeking = false;
        Paused = paused;
        Status = paused ? "已暂停" : "播放中";
        RefreshClock();
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
            var closed = ClosedDecisionBoundaries(Replay);
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
                if (decision && closed[Cursor])
                {
                    await Until(() => !executor.IsRunning);
                    lastTurn = Turn;
                    bool atTarget = seeking && (targetTurn > 0 && Turn >= targetTurn ||
                        targetDecision >= 0 && Decision + 1 >= targetDecision);
                    if (atTarget || stepToNext && Decision > stepFrom)
                        SetPaused(true);
                }
                await Until(() => true);
                switch (entry.eventType)
                {
                    case CombatReplayEventType.GameAction:
                        await Until(() => !CombatManager.Instance.EndingPlayerTurnPhaseOne &&
                            !CombatManager.Instance.EndingPlayerTurnPhaseTwo);
                        var action = entry.action!.ToGameAction(run.Players[0]);
                        if (action.ActionType == GameActionType.CombatPlayPhaseOnly)
                            await Until(() => CombatManager.Instance.DebugOnlyGetState()?.CurrentSide == CombatSide.Player);
                        if (decision)
                            Decision++;
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
                        RunManager.Instance.PlayerChoiceSynchronizer.ReceiveReplayChoice(run.Players[0],
                            entry.choiceId!.Value, entry.playerChoiceResult!.Value);
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
            Finished = true;
            Verified = verifiedCount > 0;
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

    private async Task ShowRoomAsync(RunState run, IReadOnlyList<ReplayEvent> timeline)
    {
        try
        {
            var driver = new RoomChoices(run, timeline);
            foreach (var entry in timeline)
            {
                if (seeking && Decision + 1 >= targetDecision || stepToNext && Decision > stepFrom)
                {
                    SetPaused(true);
                    Status = $"第 {Decision + 1} 个节点 · {entry.Label}";
                }
                await Until(() => true);
                // The caption has its own display time, independent of simulation speed.
                var applied = driver.ApplyAsync(Cursor);
                await Until(() => applied.IsCompleted);
                bool animated = await applied;
                Status = (animated ? "选择 · " : "记录 · ") + entry.Label;
                Decision++;
                Cursor++;
                double seconds = Math.Clamp(entry.DecisionSeconds ?? 1.4, 1, 3);
                long previous = Stopwatch.GetTimestamp();
                while (seconds > 0)
                {
                    CheckActive();
                    await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                    long now = Stopwatch.GetTimestamp();
                    if (!Paused)
                        seconds -= Stopwatch.GetElapsedTime(previous, now).TotalSeconds * (seeking ? 12 : ReplayPlayer.Speed);
                    previous = now;
                }
            }
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
        if (Closing || Finished || mismatch != null || tracker == null)
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

    private async Task Until(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            CheckActive();
            if (!Paused && ready())
                return;
            if (Paused)
                clock.Restart();
            if (clock.Elapsed > TimeSpan.FromSeconds(35))
                throw new TimeoutException($"第 {Turn} 回合、动作 {Cursor + 1} 等待超时（已校验 {verifiedCount}/{expected.Count}）。");
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
    }
}
