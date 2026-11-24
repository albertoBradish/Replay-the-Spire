using System;
using System.Linq;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireReplay.Playback;
using NativeHooks = MegaCrit.Sts2.Core.Hooks.Hook;

namespace SpireReplay.Capture;

internal static partial class CaptureHooks
{
    private static RunState? run;
    private static SceneTree? tree;
    private static bool roomReady;
    private static bool combatRoom;
    private static bool playableCombat;
    private static bool embeddedCombat;
    private static bool hasRoom;
    private static int act = -1;
    private static SerializableRun? nextCheckpoint;
    private static readonly AccessTools.FieldRef<CombatReplayWriter, CombatReplay?> CurrentReplay =
        AccessTools.FieldRefAccess<CombatReplayWriter, CombatReplay?>("_replay");
    private static readonly AccessTools.FieldRef<RunManager, long> StartTime =
        AccessTools.FieldRefAccess<RunManager, long>("_startTime");

    internal static void Install(Harmony harmony)
    {
        void Hook(Type type, string method, string target, bool before = false)
        {
            var original = AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(type.FullName, method);
            var handler = new HarmonyMethod(typeof(CaptureHooks), target);
            harmony.Patch(original, prefix: before ? handler : null, postfix: before ? null : handler);
        }

        Hook(typeof(RunManager), nameof(RunManager.Launch), nameof(Launched));
        Hook(typeof(RunManager), "EnterRoomInternal", nameof(EnteringRoom), true);
        Hook(typeof(EventRoom), nameof(EventRoom.Resume), nameof(EventResumed));
        Hook(typeof(CombatReplayWriter), nameof(CombatReplayWriter.RecordInitialState), nameof(Checkpoint));
        Hook(typeof(CombatReplayWriter), nameof(CombatReplayWriter.WriteReplay), nameof(BeforeWrite), true);
        Hook(typeof(CombatReplayWriter), nameof(CombatReplayWriter.WriteReplay), nameof(AfterWrite));
        Hook(typeof(RunManager), nameof(RunManager.OnEnded), nameof(Ended), true);
        Hook(typeof(RunManager), nameof(RunManager.CleanUp), nameof(Detach));
        Hook(typeof(CombatHistory), nameof(CombatHistory.CardPlayFinished), nameof(CardPlayed));
        // The history omits the finishing blow once IsEnding flips. This native dispatch receives
        // every resolved hit, including redirected damage and fully blocked hits, before death cleanup.
        Hook(typeof(NativeHooks), nameof(NativeHooks.AfterDamageGiven), nameof(Damaged), true);
        Hook(typeof(CombatHistory), nameof(CombatHistory.BlockGained), nameof(Blocked));
        Hook(typeof(CombatHistory), nameof(CombatHistory.CardDrawn), nameof(Drawn));
        InstallChoices(Hook);
    }

    private static bool Recording => run != null && !ReplayPlayer.IsActive;

    // Observers must never interrupt a player's action, including on a disk or UI error.
    private static void Observe(Action action)
    {
        try { action(); }
        catch (Exception error) { GD.PushWarning($"[SpireReplay] {error.GetType().Name}: {error.Message}"); }
    }

    private static void Launched(RunManager __instance, RunState __result) => Observe(() =>
    {
        Detach();
        if (ReplayPlayer.IsActive || __instance.NetService.Type != NetGameType.Singleplayer || __result.Players.Count != 1)
            return;
        var player = __result.Players[0];
        var seed = __result.Rng.StringSeed;
        var start = StartTime(__instance);
        var build = GameEnvironment.Build;
        var key = string.Join("\u001f", start, seed, player.Character.Id, build, GameEnvironment.Mods);
        ReplayStore.BeginRun(key, player.Character.Title.GetFormattedText(), build, seed);
        run = __result;
        __instance.RoomEntered += RoomEntered;
        __instance.ActionQueueSet.ActionEnqueued += InputQueued;
        __instance.RestSiteSynchronizer.BeforePlayerOptionChosen += RestStarted;
        __instance.RestSiteSynchronizer.AfterPlayerOptionChosen += RestFinished;
        CombatManager.Instance.TurnStarted += NewTurn;
        tree = Engine.GetMainLoop() as SceneTree;
        if (tree != null) tree.ProcessFrame += SampleClock;
    });

    private static void Checkpoint(SerializableRun serializableRun)
    {
        if (Recording) nextCheckpoint = serializableRun;
    }

    private static void EnteringRoom(AbstractRoom room, bool isRestoringRoomStackBase) => Observe(() =>
    {
        if (!Recording || isRestoringRoomStackBase || (room is MapRoom && hasRoom && act == run!.CurrentActIndex)) return;
        act = run!.CurrentActIndex;
        roomReady = false;
        combatRoom = room is CombatRoom;
        if (room is CombatRoom { ParentEventId: not null, IsPreFinished: false } nested &&
            hasRoom && run!.CurrentRoom is EventRoom parent && parent.ModelId == nested.ParentEventId)
        {
            // The game's writer keeps this floor's pre-event snapshot and all combat
            // actions. The original event choices recreate the mutable parent and RNG.
            embeddedCombat = playableCombat = true;
            ResetChoices();
            ReplayStore.Add("event-battle", nested.Encounter.Title.GetFormattedText(),
                actionKey: $"{parent.ModelId}|{nested.Encounter.Id}");
            return;
        }
        embeddedCombat = false;
        playableCombat = room is CombatRoom battle && battle.ParentEventId == null && run!.CurrentRoomCount == 0 && !room.IsPreFinished;
        ResetChoices();
        var title = room switch
        {
            CombatRoom fight => fight.Encounter.Title.GetFormattedText(),
            EventRoom ev => ev.CanonicalEvent.Title.GetFormattedText(),
            MerchantRoom => "商店",
            RestSiteRoom => "营地",
            TreasureRoom => "宝箱",
            MapRoom => "地图",
            _ => room.RoomType.ToString()
        };
        ReplayStore.BeginRoom(run!.TotalFloor, title);
        hasRoom = true;
        if (nextCheckpoint == null && room is MapRoom)
            nextCheckpoint = RunManager.Instance.ToSave(null);
        if (nextCheckpoint == null && room is EventRoom { CanonicalEvent: TheArchitect } ending)
        {
            nextCheckpoint = RunManager.Instance.ToSave(null);
            var progress = SaveManager.Instance.Progress;
            int heroWins = progress.GetStatsForCharacter(run.Players[0].Character.Id)?.TotalWins ?? 0;
            ReplayStore.Add("direct-event", title, amount: progress.Wins, actionKey: ending.ModelId.ToString(), actionIndex: heroWins);
        }
        if (combatRoom && !playableCombat && !room.IsPreFinished)
            ReplayStore.Add("event", "事件内战斗");
        if (nextCheckpoint != null)
        {
            var snapshot = nextCheckpoint;
            nextCheckpoint = null;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot.Anonymized(), JsonSerializationUtility.GetTypeInfo<SerializableRun>());
            ReplayStore.SaveCheckpoint(bytes);
        }
    });

    private static void RoomEntered()
    {
        roomReady = true;
        RestartClock();
    }

    private static void EventResumed(EventRoom __instance)
    {
        if (!Recording) return;
        Observe(() =>
        {
            combatRoom = playableCombat = false;
            if (!embeddedCombat)
                ReplayStore.BeginRoom(run!.TotalFloor, __instance.CanonicalEvent.Title.GetFormattedText());
            ReplayStore.Add(embeddedCombat ? "event-resume" : "event", __instance.CanonicalEvent.Title.GetFormattedText(),
                actionKey: __instance.ModelId.ToString());
            embeddedCombat = false;
            ResetChoices();
            roomReady = true;
        });
    }

    private static void BeforeWrite(CombatReplay? ____replay, out bool __state)
    {
        __state = Recording && playableCombat && ____replay != null;
    }

    private static void AfterWrite(bool __state, PacketWriter ____writer)
    {
        if (__state) Observe(() => ReplayStore.SaveCombat(____writer.Buffer.AsSpan(0, ____writer.BytePosition).ToArray()));
    }

    private static void Ended(RunManager __instance, bool isVictory) => Observe(() =>
    {
        if (!Recording) return;
        // Defeat and abandon can precede the normal combat-end write.
        var replay = CurrentReplay(__instance.CombatReplayWriter);
        if (playableCombat && replay != null)
        {
            var packet = new PacketWriter();
            packet.Write(replay.Anonymized());
            ReplayStore.SaveCombat(packet.Buffer.AsSpan(0, packet.BytePosition).ToArray());
        }
        ReplayStore.EndRun(__instance.IsAbandoned ? "放弃" : isVictory ? "胜利" : "战败");
        Detach();
    });

    private static void Detach()
    {
        if (tree != null && GodotObject.IsInstanceValid(tree)) tree.ProcessFrame -= SampleClock;
        tree = null;
        if (run != null)
        {
            var manager = RunManager.Instance;
            manager.RoomEntered -= RoomEntered;
            if (manager.ActionQueueSet != null) manager.ActionQueueSet.ActionEnqueued -= InputQueued;
            if (manager.RestSiteSynchronizer != null)
            {
                manager.RestSiteSynchronizer.BeforePlayerOptionChosen -= RestStarted;
                manager.RestSiteSynchronizer.AfterPlayerOptionChosen -= RestFinished;
            }
            CombatManager.Instance.TurnStarted -= NewTurn;
        }
        run = null;
        act = -1;
        roomReady = combatRoom = playableCombat = embeddedCombat = hasRoom = false;
        nextCheckpoint = null;
        ResetChoices();
    }

    private static void NewTurn(CombatState state)
    {
        if (!Recording || state.CurrentSide != CombatSide.Player) return;
        Observe(() => ReplayStore.Add("turn", $"第 {state.RoundNumber} 回合", state.RoundNumber));
        RestartClock();
    }

    private static void CardPlayed(ICombatState combatState, CardPlay cardPlay)
    {
        if (!Recording) return;
        Observe(() => ReplayStore.Add(cardPlay.IsAutoPlay || cardPlay.PlayIndex > 0 ? "auto" : "play",
            cardPlay.Card.Title, combatState.RoundNumber, cardPlay.Card.Id.ToString(), 1, actionIndex: ExecutingDecision));
    }

    private static void Damaged(ICombatState combatState, DamageResult results, CardModel? cardSource)
    {
        if (!Recording || results.Receiver.Side != CombatSide.Enemy ||
            (results.UnblockedDamage == 0 && results.BlockedDamage == 0)) return;
        Observe(() =>
        {
            var id = cardSource?.Id.ToString() ?? "";
            var label = cardSource?.Title ?? "其他来源";
            // UnblockedDamage is the actual HP delta. The native result reports overkill separately.
            if (results.UnblockedDamage > 0)
                ReplayStore.Add("damage", label, combatState.RoundNumber, id, results.UnblockedDamage, actionIndex: ExecutingDecision);
            if (results.BlockedDamage > 0)
                ReplayStore.Add("blocked", label, combatState.RoundNumber, id, results.BlockedDamage, actionIndex: ExecutingDecision);
        });
    }

    private static void Blocked(ICombatState combatState, Creature receiver, int amount, CardPlay? cardPlay)
    {
        if (!Recording || receiver.Side != CombatSide.Player || amount <= 0) return;
        Observe(() => ReplayStore.Add("block", cardPlay?.Card.Title ?? "其他来源", combatState.RoundNumber,
            cardPlay?.Card.Id.ToString() ?? "", amount, actionIndex: ExecutingDecision));
    }

    private static void Drawn(ICombatState combatState, CardModel card)
    {
        if (Recording) Observe(() => ReplayStore.Add("draw", card.Title, combatState.RoundNumber, card.Id.ToString(), 1, actionIndex: ExecutingDecision));
    }
}
