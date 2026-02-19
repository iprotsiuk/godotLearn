// Scripts/Net/NetCodec.Control.cs
using Godot;

namespace NetRunnerSlice.Net;

public static partial class NetCodec
{
    public static void WriteControlHello(byte[] packet)
    {
        packet[0] = (byte)PacketType.Control;
        packet[1] = (byte)ControlType.Hello;
        WriteUInt(packet, 2, NetConstants.ProtocolVersion);
    }

    public static void WriteControlWelcome(
        byte[] packet,
        int assignedPeer,
        uint serverTick,
        int serverTickRate,
        int clientTickRate,
        int snapshotRate,
        int interpolationDelayMs,
        int maxExtrapolationMs,
        int reconcileSmoothMs,
        float reconcileSnapThreshold,
        float pitchClampDegrees,
        float moveSpeed,
        float groundAcceleration,
        float airAcceleration,
        float airControlFactor,
        float jumpVelocity,
        float gravity,
        int serverInputDelayTicks,
        float floorSnapLength,
        float groundStickVelocity,
        int wallRunMaxTicks,
        int slideMaxTicks,
        float wallRunGravityScale,
        float wallJumpUpVelocity,
        float wallJumpAwayVelocity)
    {
        packet[0] = (byte)PacketType.Control;
        packet[1] = (byte)ControlType.Welcome;
        WriteUInt(packet, 2, NetConstants.ProtocolVersion);
        WriteInt(packet, 6, assignedPeer);
        WriteInt(packet, 10, serverTickRate);
        WriteInt(packet, 14, clientTickRate);
        WriteInt(packet, 18, snapshotRate);
        WriteInt(packet, 22, interpolationDelayMs);
        WriteInt(packet, 26, maxExtrapolationMs);
        WriteInt(packet, 30, reconcileSmoothMs);
        WriteFloat(packet, 34, reconcileSnapThreshold);
        WriteFloat(packet, 38, pitchClampDegrees);
        WriteFloat(packet, 42, moveSpeed);
        WriteFloat(packet, 46, groundAcceleration);
        WriteFloat(packet, 50, airAcceleration);
        WriteFloat(packet, 54, airControlFactor);
        WriteFloat(packet, 58, jumpVelocity);
        WriteFloat(packet, 62, gravity);
        WriteInt(packet, 66, serverInputDelayTicks);
        WriteFloat(packet, 70, floorSnapLength);
        WriteFloat(packet, 74, groundStickVelocity);
        WriteUInt(packet, 78, serverTick);
        packet[82] = (byte)Mathf.Clamp(wallRunMaxTicks, 0, 255);
        packet[83] = (byte)Mathf.Clamp(slideMaxTicks, 0, 255);
        WriteFloat(packet, 84, wallRunGravityScale);
        WriteFloat(packet, 88, wallJumpUpVelocity);
        WriteFloat(packet, 92, wallJumpAwayVelocity);
    }

    public static void WriteControlPing(byte[] packet, ushort pingSeq, uint clientTimeMs)
    {
        packet[0] = (byte)PacketType.Control;
        packet[1] = (byte)ControlType.Ping;
        WriteUShort(packet, 2, pingSeq);
        WriteUInt(packet, 4, clientTimeMs);
    }

    public static void WriteControlPong(byte[] packet, ushort pingSeq, uint clientTimeMs, uint serverTick)
    {
        packet[0] = (byte)PacketType.Control;
        packet[1] = (byte)ControlType.Pong;
        WriteUShort(packet, 2, pingSeq);
        WriteUInt(packet, 4, clientTimeMs);
        WriteUInt(packet, 8, serverTick);
    }

    public static void WriteControlDelayUpdate(byte[] packet, int delayTicks)
    {
        packet[0] = (byte)PacketType.Control;
        packet[1] = (byte)ControlType.DelayUpdate;
        WriteInt(packet, 2, delayTicks);
    }

    public static void WriteControlResyncHint(byte[] packet, uint serverTick)
    {
        packet[0] = (byte)PacketType.Control;
        packet[1] = (byte)ControlType.ResyncHint;
        WriteUInt(packet, 2, serverTick);
    }

    public static void WriteControlMatchConfig(byte[] packet, MatchConfig config)
    {
        System.Array.Clear(packet, 0, NetConstants.ControlPacketBytes);
        packet[0] = (byte)PacketType.Control;
        packet[1] = (byte)ControlType.MatchConfig;
        WriteInt(packet, 2, (int)config.ModeId);
        WriteInt(packet, 6, config.RoundTimeSec);
    }

    public static void WriteControlMatchState(byte[] packet, MatchState state)
    {
        System.Array.Clear(packet, 0, NetConstants.ControlPacketBytes);
        packet[0] = (byte)PacketType.Control;
        packet[1] = (byte)ControlType.MatchState;
        WriteInt(packet, 2, state.RoundIndex);
        packet[6] = (byte)state.Phase;
        WriteUInt(packet, 7, state.PhaseEndTick);
    }

    public static void WriteControlTagStateFull(byte[] packet, TagState state)
    {
        System.Array.Clear(packet, 0, NetConstants.ControlPacketBytes);
        packet[0] = (byte)PacketType.Control;
        packet[1] = (byte)ControlType.TagStateFull;
        WriteInt(packet, 2, state.RoundIndex);
        WriteInt(packet, 6, state.ItPeerId);
        WriteUInt(packet, 10, state.ItCooldownEndTick);
        WriteUInt(packet, 14, state.TagAppliedTick);
        WriteInt(packet, 18, state.TaggerPeerId);
        WriteInt(packet, 22, state.TaggedPeerId);
    }

    public static void WriteControlTagStateDelta(
        byte[] packet,
        int itPeerId,
        uint itCooldownEndTick,
        uint tagAppliedTick,
        int taggerPeerId,
        int taggedPeerId)
    {
        System.Array.Clear(packet, 0, NetConstants.ControlPacketBytes);
        packet[0] = (byte)PacketType.Control;
        packet[1] = (byte)ControlType.TagStateDelta;
        WriteInt(packet, 2, itPeerId);
        WriteUInt(packet, 6, itCooldownEndTick);
        WriteUInt(packet, 10, tagAppliedTick);
        WriteInt(packet, 14, taggerPeerId);
        WriteInt(packet, 18, taggedPeerId);
    }

    public static void WriteControlInventoryState(byte[] packet, int peerId, byte itemId, byte charges, uint cooldownEndTick)
    {
        System.Array.Clear(packet, 0, NetConstants.ControlPacketBytes);
        packet[0] = (byte)PacketType.Control;
        packet[1] = (byte)ControlType.InventoryState;
        WriteInt(packet, 2, peerId);
        packet[6] = itemId;
        packet[7] = charges;
        WriteUInt(packet, 8, cooldownEndTick);
    }

    public static void WriteControlPickupState(byte[] packet, int pickupId, bool isActive)
    {
        System.Array.Clear(packet, 0, NetConstants.ControlPacketBytes);
        packet[0] = (byte)PacketType.Control;
        packet[1] = (byte)ControlType.PickupState;
        WriteInt(packet, 2, pickupId);
        packet[6] = isActive ? (byte)1 : (byte)0;
    }

    public static void WriteControlFreezeState(byte[] packet, int targetPeerId, uint frozenUntilTick)
    {
        System.Array.Clear(packet, 0, NetConstants.ControlPacketBytes);
        packet[0] = (byte)PacketType.Control;
        packet[1] = (byte)ControlType.FreezeState;
        WriteInt(packet, 2, targetPeerId);
        WriteUInt(packet, 6, frozenUntilTick);
    }

    public static bool TryReadControl(ReadOnlySpan<byte> packet, out ControlType type)
    {
        type = 0;
        if (packet.Length < NetConstants.ControlPacketBytes || packet[0] != (byte)PacketType.Control)
        {
            return false;
        }

        type = (ControlType)packet[1];
        return true;
    }

    public static uint ReadControlProtocol(ReadOnlySpan<byte> packet) => ReadUInt(packet, 2);

    public static int ReadControlAssignedPeer(ReadOnlySpan<byte> packet) => ReadInt(packet, 6);

    public static int ReadControlServerTickRate(ReadOnlySpan<byte> packet) => ReadInt(packet, 10);

    public static int ReadControlClientTickRate(ReadOnlySpan<byte> packet) => ReadInt(packet, 14);

    public static int ReadControlSnapshotRate(ReadOnlySpan<byte> packet) => ReadInt(packet, 18);

    public static int ReadControlInterpolationDelayMs(ReadOnlySpan<byte> packet) => ReadInt(packet, 22);

    public static int ReadControlMaxExtrapolationMs(ReadOnlySpan<byte> packet) => ReadInt(packet, 26);

    public static int ReadControlReconcileSmoothMs(ReadOnlySpan<byte> packet) => ReadInt(packet, 30);

    public static float ReadControlReconcileSnapThreshold(ReadOnlySpan<byte> packet) => ReadFloat(packet, 34);

    public static float ReadControlPitchClampDegrees(ReadOnlySpan<byte> packet) => ReadFloat(packet, 38);

    public static float ReadControlMoveSpeed(ReadOnlySpan<byte> packet) => ReadFloat(packet, 42);

    public static float ReadControlGroundAcceleration(ReadOnlySpan<byte> packet) => ReadFloat(packet, 46);

    public static float ReadControlAirAcceleration(ReadOnlySpan<byte> packet) => ReadFloat(packet, 50);

    public static float ReadControlAirControlFactor(ReadOnlySpan<byte> packet) => ReadFloat(packet, 54);

    public static float ReadControlJumpVelocity(ReadOnlySpan<byte> packet) => ReadFloat(packet, 58);

    public static float ReadControlGravity(ReadOnlySpan<byte> packet) => ReadFloat(packet, 62);

    public static int ReadControlServerInputDelayTicks(ReadOnlySpan<byte> packet) => ReadInt(packet, 66);

    public static float ReadControlFloorSnapLength(ReadOnlySpan<byte> packet) => ReadFloat(packet, 70);

    public static float ReadControlGroundStickVelocity(ReadOnlySpan<byte> packet) => ReadFloat(packet, 74);

    public static uint ReadControlWelcomeServerTick(ReadOnlySpan<byte> packet) => ReadUInt(packet, 78);

    public static int ReadControlWallRunMaxTicks(ReadOnlySpan<byte> packet) => packet[82];

    public static int ReadControlSlideMaxTicks(ReadOnlySpan<byte> packet) => packet[83];

    public static float ReadControlWallRunGravityScale(ReadOnlySpan<byte> packet) => ReadFloat(packet, 84);

    public static float ReadControlWallJumpUpVelocity(ReadOnlySpan<byte> packet) => ReadFloat(packet, 88);

    public static float ReadControlWallJumpAwayVelocity(ReadOnlySpan<byte> packet) => ReadFloat(packet, 92);

    public static ushort ReadControlPingSeq(ReadOnlySpan<byte> packet) => ReadUShort(packet, 2);

    public static uint ReadControlClientTime(ReadOnlySpan<byte> packet) => ReadUInt(packet, 4);

    public static uint ReadControlServerTick(ReadOnlySpan<byte> packet) => ReadUInt(packet, 8);

    public static int ReadControlDelayTicks(ReadOnlySpan<byte> packet) => ReadInt(packet, 2);

    public static uint ReadControlResyncHintTick(ReadOnlySpan<byte> packet) => ReadUInt(packet, 2);

    public static MatchConfig ReadControlMatchConfig(ReadOnlySpan<byte> packet)
    {
        return new MatchConfig
        {
            ModeId = (GameModes.GameModeId)ReadInt(packet, 2),
            RoundTimeSec = ReadInt(packet, 6)
        };
    }

    public static MatchState ReadControlMatchState(ReadOnlySpan<byte> packet)
    {
        return new MatchState
        {
            RoundIndex = ReadInt(packet, 2),
            Phase = (MatchPhase)packet[6],
            PhaseEndTick = ReadUInt(packet, 7)
        };
    }

    public static TagState ReadControlTagStateFull(ReadOnlySpan<byte> packet)
    {
        return new TagState
        {
            RoundIndex = ReadInt(packet, 2),
            ItPeerId = ReadInt(packet, 6),
            ItCooldownEndTick = ReadUInt(packet, 10),
            TagAppliedTick = ReadUInt(packet, 14),
            TaggerPeerId = ReadInt(packet, 18),
            TaggedPeerId = ReadInt(packet, 22)
        };
    }

    public static int ReadControlTagStateDeltaItPeer(ReadOnlySpan<byte> packet) => ReadInt(packet, 2);

    public static uint ReadControlTagStateDeltaCooldownEndTick(ReadOnlySpan<byte> packet) => ReadUInt(packet, 6);

    public static uint ReadControlTagStateDeltaAppliedTick(ReadOnlySpan<byte> packet) => ReadUInt(packet, 10);

    public static int ReadControlTagStateDeltaTaggerPeer(ReadOnlySpan<byte> packet) => ReadInt(packet, 14);

    public static int ReadControlTagStateDeltaTaggedPeer(ReadOnlySpan<byte> packet) => ReadInt(packet, 18);

    public static int ReadControlInventoryPeerId(ReadOnlySpan<byte> packet) => ReadInt(packet, 2);

    public static byte ReadControlInventoryItemId(ReadOnlySpan<byte> packet) => packet[6];

    public static byte ReadControlInventoryCharges(ReadOnlySpan<byte> packet) => packet[7];

    public static uint ReadControlInventoryCooldownEndTick(ReadOnlySpan<byte> packet) => ReadUInt(packet, 8);

    public static int ReadControlPickupStatePickupId(ReadOnlySpan<byte> packet) => ReadInt(packet, 2);

    public static bool ReadControlPickupStateIsActive(ReadOnlySpan<byte> packet) => packet[6] != 0;

    public static int ReadControlFreezeStateTargetPeerId(ReadOnlySpan<byte> packet) => ReadInt(packet, 2);

    public static uint ReadControlFreezeStateFrozenUntilTick(ReadOnlySpan<byte> packet) => ReadUInt(packet, 6);
}
