using System.Buffers.Binary;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace SpireReplay.Playback;

internal static class ReplayDecoder
{
    [ThreadStatic] private static PacketReader? activeReader;

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(PacketReader), nameof(PacketReader.ReadString)),
            prefix: new HarmonyMethod(typeof(ReplayDecoder), nameof(CheckStringSize)));
    }

    private static void CheckStringSize(PacketReader __instance)
    {
        if (!ReferenceEquals(__instance, activeReader))
            return;
        int position = __instance.BitPosition;
        int offset = position / 8;
        int shift = position % 8;
        var bytes = __instance.Buffer.AsSpan(offset);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes) >> shift;
        if (shift != 0)
            length |= (uint)bytes[4] << (32 - shift);
        long remainingBits = (long)__instance.Buffer.Length * 8 - position - 32;
        if (length > 4 * 1024 * 1024 || (long)length * 8 > remainingBits)
            throw new InvalidDataException("回放文本长度无效。");
    }

    internal static CombatReplay Read(byte[] data)
    {
        if (data.Length is < 16 or > 32 * 1024 * 1024)
            throw new InvalidDataException("战斗记录大小无效。");
        var reader = new PacketReader();
        reader.Reset(data);
        activeReader = reader;
        CombatReplay replay;
        try
        {
            // Validate the header before resolving model or action IDs against the local game.
            string version = reader.ReadString();
            string commit = reader.ReadString();
            uint modelIds = reader.ReadUInt();
            var build = ReleaseInfoManager.Instance.ReleaseInfo;
            if (build == null || version != build.Version || commit != build.Commit)
                throw new InvalidDataException($"这段回放需要游戏 {version}（{commit}）。");
            if (modelIds != ModelIdSerializationCache.Hash)
                throw new InvalidDataException("这段回放需要与录制时一致的游戏内容和模组。");
            reader.Reset(data);
            replay = reader.Read<CombatReplay>();
            if ((reader.BitPosition + 7) / 8 != data.Length)
                throw new InvalidDataException("战斗记录数据不完整。");
        }
        finally
        {
            activeReader = null;
        }
        if (replay.serializableRun.Players.Count != 1)
            throw new InvalidDataException("此版本支持单人回放。");
        foreach (var item in replay.events)
        {
            if (item.playerId is ulong id && id != replay.serializableRun.Players[0].NetId)
                throw new InvalidDataException("战斗操作引用了未知玩家。");
            if (item.eventType != CombatReplayEventType.GameAction)
                continue;
            if (item.action is not (NetPlayCardAction or NetEndPlayerTurnAction or
                NetReadyToBeginEnemyTurnAction or NetUndoEndPlayerTurnAction or
                NetUsePotionAction or NetDiscardPotionGameAction))
                throw new InvalidDataException($"回放包含不支持的战斗操作：{item.action?.GetType().Name}。");
        }
        return replay;
    }
}
