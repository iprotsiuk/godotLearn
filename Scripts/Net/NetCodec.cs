// Scripts/Net/NetCodec.cs
using System.Buffers.Binary;
using Godot;
namespace NetRunnerSlice.Net;
public static partial class NetCodec
{
    public static void WriteInputBundle(byte[] packet, ReadOnlySpan<InputCommand> commands)
    {
        packet[0] = (byte)PacketType.InputBundle;
        int count = Mathf.Min(commands.Length, NetConstants.MaxInputRedundancy);
        packet[1] = (byte)count;
        int offset = 2;
        for (int i = 0; i < NetConstants.MaxInputRedundancy; i++)
        {
            InputCommand cmd = i < count ? commands[i] : default;
            WriteInputCommand(packet, ref offset, in cmd);
        }
    }

    public static bool TryReadInputBundle(
        ReadOnlySpan<byte> packet,
        Span<InputCommand> commandOut,
        out int count)
    {
        count = 0;
        if (packet.Length < NetConstants.InputPacketBytes || packet[0] != (byte)PacketType.InputBundle)
        {
            return false;
        }

        count = Mathf.Min(packet[1], NetConstants.MaxInputRedundancy);
        int offset = 2;
        for (int i = 0; i < count; i++)
        {
            if (!ReadInputCommand(packet, ref offset, out commandOut[i]))
            {
                count = i;
                return false;
            }
        }

        return true;
    }

    public static void WriteSnapshot(byte[] packet, uint serverTick, ReadOnlySpan<PlayerStateSnapshot> states)
    {
        packet[0] = (byte)PacketType.Snapshot;
        WriteUInt(packet, 1, serverTick);

        int count = Mathf.Min(states.Length, NetConstants.MaxPlayers);
        packet[5] = (byte)count;

        int offset = 6;
        for (int i = 0; i < NetConstants.MaxPlayers; i++)
        {
            PlayerStateSnapshot state = i < count ? states[i] : default;
            WriteState(packet, ref offset, in state);
        }
    }

    public static bool TryReadSnapshot(
        ReadOnlySpan<byte> packet,
        out uint serverTick,
        Span<PlayerStateSnapshot> stateOut,
        out int count)
    {
        serverTick = 0;
        count = 0;
        if (packet.Length < NetConstants.SnapshotPacketBytes || packet[0] != (byte)PacketType.Snapshot)
        {
            return false;
        }

        serverTick = ReadUInt(packet, 1);
        count = Mathf.Min(packet[5], NetConstants.MaxPlayers);
        int offset = 6;
        for (int i = 0; i < count; i++)
        {
            if (!ReadState(packet, ref offset, out stateOut[i]))
            {
                count = i;
                return false;
            }
        }

        return true;
    }

    private static void WriteInputCommand(byte[] packet, ref int offset, in InputCommand cmd)
    {
        WriteUInt(packet, offset, cmd.Seq);
        offset += 4;
        WriteUInt(packet, offset, cmd.InputTick);
        offset += 4;
        WriteUInt(packet, offset, cmd.InputEpoch);
        offset += 4;
        WriteFloat(packet, offset, cmd.DtFixed);
        offset += 4;
        WriteFloat(packet, offset, cmd.MoveAxes.X);
        offset += 4;
        WriteFloat(packet, offset, cmd.MoveAxes.Y);
        offset += 4;
        packet[offset++] = (byte)cmd.Buttons;
        WriteFloat(packet, offset, cmd.Yaw);
        offset += 4;
        WriteFloat(packet, offset, cmd.Pitch);
        offset += 4;
    }

    private static bool ReadInputCommand(ReadOnlySpan<byte> packet, ref int offset, out InputCommand cmd)
    {
        cmd = default;
        if ((offset + NetConstants.InputCommandBytes) > packet.Length)
        {
            return false;
        }

        cmd.Seq = ReadUInt(packet, offset);
        offset += 4;
        cmd.InputTick = ReadUInt(packet, offset);
        offset += 4;
        cmd.InputEpoch = ReadUInt(packet, offset);
        offset += 4;
        cmd.DtFixed = ReadFloat(packet, offset);
        offset += 4;
        cmd.MoveAxes.X = ReadFloat(packet, offset);
        offset += 4;
        cmd.MoveAxes.Y = ReadFloat(packet, offset);
        offset += 4;
        cmd.Buttons = (InputButtons)packet[offset++];
        cmd.Yaw = ReadFloat(packet, offset);
        offset += 4;
        cmd.Pitch = ReadFloat(packet, offset);
        offset += 4;
        return true;
    }

    private static void WriteState(byte[] packet, ref int offset, in PlayerStateSnapshot state)
    {
        WriteInt(packet, offset, state.PeerId);
        offset += 4;
        WriteUInt(packet, offset, state.LastProcessedSeqForThatClient);
        offset += 4;
        WriteVector3(packet, ref offset, state.Pos);
        WriteVector3(packet, ref offset, state.Vel);
        WriteFloat(packet, offset, state.Yaw);
        offset += 4;
        WriteFloat(packet, offset, state.Pitch);
        offset += 4;
        packet[offset++] = state.Grounded ? (byte)1 : (byte)0;
        WriteUInt(packet, offset, state.DroppedOldInputCount);
        offset += 4;
        WriteUInt(packet, offset, state.DroppedFutureInputCount);
        offset += 4;
        WriteUInt(packet, offset, state.TicksUsedBufferedInput);
        offset += 4;
        WriteUInt(packet, offset, state.TicksUsedHoldLast);
        offset += 4;
        WriteUInt(packet, offset, state.TicksUsedNeutral);
        offset += 4;
        WriteUInt(packet, offset, state.MissingInputStreakCurrent);
        offset += 4;
        WriteUInt(packet, offset, state.MissingInputStreakMax);
        offset += 4;
        WriteInt(packet, offset, state.EffectiveDelayTicks);
        offset += 4;
        WriteFloat(packet, offset, state.ServerPeerRttMs);
        offset += 4;
        WriteFloat(packet, offset, state.ServerPeerJitterMs);
        offset += 4;
    }

    private static bool ReadState(ReadOnlySpan<byte> packet, ref int offset, out PlayerStateSnapshot state)
    {
        state = default;
        if ((offset + NetConstants.SnapshotStateBytes) > packet.Length)
        {
            return false;
        }

        state.PeerId = ReadInt(packet, offset);
        offset += 4;
        state.LastProcessedSeqForThatClient = ReadUInt(packet, offset);
        offset += 4;
        state.Pos = ReadVector3(packet, ref offset);
        state.Vel = ReadVector3(packet, ref offset);
        state.Yaw = ReadFloat(packet, offset);
        offset += 4;
        state.Pitch = ReadFloat(packet, offset);
        offset += 4;
        state.Grounded = packet[offset++] != 0;
        state.DroppedOldInputCount = ReadUInt(packet, offset);
        offset += 4;
        state.DroppedFutureInputCount = ReadUInt(packet, offset);
        offset += 4;
        state.TicksUsedBufferedInput = ReadUInt(packet, offset);
        offset += 4;
        state.TicksUsedHoldLast = ReadUInt(packet, offset);
        offset += 4;
        state.TicksUsedNeutral = ReadUInt(packet, offset);
        offset += 4;
        state.MissingInputStreakCurrent = ReadUInt(packet, offset);
        offset += 4;
        state.MissingInputStreakMax = ReadUInt(packet, offset);
        offset += 4;
        state.EffectiveDelayTicks = ReadInt(packet, offset);
        offset += 4;
        state.ServerPeerRttMs = ReadFloat(packet, offset);
        offset += 4;
        state.ServerPeerJitterMs = ReadFloat(packet, offset);
        offset += 4;
        return true;
    }

    private static void WriteVector3(byte[] packet, ref int offset, Vector3 value)
    {
        WriteFloat(packet, offset, value.X);
        offset += 4;
        WriteFloat(packet, offset, value.Y);
        offset += 4;
        WriteFloat(packet, offset, value.Z);
        offset += 4;
    }

    private static Vector3 ReadVector3(ReadOnlySpan<byte> packet, ref int offset)
    {
        float x = ReadFloat(packet, offset);
        offset += 4;
        float y = ReadFloat(packet, offset);
        offset += 4;
        float z = ReadFloat(packet, offset);
        offset += 4;
        return new Vector3(x, y, z);
    }

    private static void WriteInt(byte[] packet, int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset, 4), value);
    }

    private static int ReadInt(ReadOnlySpan<byte> packet, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(offset, 4));
    }

    private static void WriteUInt(byte[] packet, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset, 4), value);
    }

    private static uint ReadUInt(ReadOnlySpan<byte> packet, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(offset, 4));
    }

    private static void WriteUShort(byte[] packet, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(offset, 2), value);
    }

    private static ushort ReadUShort(ReadOnlySpan<byte> packet, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(offset, 2));
    }

    private static void WriteFloat(byte[] packet, int offset, float value)
    {
        WriteUInt(packet, offset, System.BitConverter.SingleToUInt32Bits(value));
    }

    private static float ReadFloat(ReadOnlySpan<byte> packet, int offset)
    {
        return System.BitConverter.UInt32BitsToSingle(ReadUInt(packet, offset));
    }
}
