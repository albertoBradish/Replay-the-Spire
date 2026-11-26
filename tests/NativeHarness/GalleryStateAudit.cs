using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Runs;
using SpireReplay.Playback;

namespace ReplayHarness;

internal static class GalleryStateAudit
{
    private static readonly Type Session = typeof(ReplayPlayer).Assembly.GetType("SpireReplay.Playback.ReplaySession", true)!;
    private static readonly System.Reflection.FieldInfo VerifiedCount = AccessTools.Field(Session, "verifiedCount");
    private static bool enabled;
    private static int restoredRooms, verifiedChecksums;
    private static bool rngMismatch;

    internal static void Install(Harmony harmony)
    {
        enabled = true;
        harmony.Patch(AccessTools.Method(typeof(RunManager), nameof(RunManager.SetUpReplay)),
            prefix: new HarmonyMethod(typeof(GalleryStateAudit), nameof(CheckEntryRng)));
        harmony.Patch(AccessTools.Method(Session, "Verify"),
            prefix: new HarmonyMethod(typeof(GalleryStateAudit), nameof(StartCheck)),
            postfix: new HarmonyMethod(typeof(GalleryStateAudit), nameof(FinishCheck)));
    }

    private static bool Same<T>(T actual, T recorded) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(actual), JsonSerializer.SerializeToElement(recorded));

    private static void CheckEntryRng(RunState state, CombatReplay replay)
    {
        var recording = replay.serializableRun;
        ++restoredRooms;
        rngMismatch |= !Same(state.Rng.ToSerializable(), recording.SerializableRng) ||
            !Same(state.Players.Single().PlayerRng.ToSerializable(), recording.Players.Single().Rng);
    }

    private static void StartCheck(object __instance, out int __state) =>
        __state = (int)VerifiedCount.GetValue(__instance)!;

    private static void FinishCheck(object __instance, int __state) =>
        verifiedChecksums += (int)VerifiedCount.GetValue(__instance)! - __state;

    internal static void Report(int expectedRooms)
    {
        if (!enabled) return;
        if (restoredRooms != expectedRooms || rngMismatch || verifiedChecksums == 0)
            throw new InvalidDataException($"Gallery state audit failed. Restored {restoredRooms}/{expectedRooms}, matched {verifiedChecksums} checksums, RNG mismatch: {rngMismatch}.");
        GD.Print($"REPLAY_GALLERY_STATE_PASS rooms={restoredRooms} checksums={verifiedChecksums}");
    }
}
