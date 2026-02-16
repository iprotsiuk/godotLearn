// Scripts/Net/NetCodec.Fire.cs
using Godot;

namespace NetRunnerSlice.Net;

public static partial class NetCodec
{
    public static void WriteFire(byte[] packet, in FireRequest request)
    {
        packet[0] = (byte)PacketType.Fire;
        WriteUInt(packet, 1, request.FireSeq);
        WriteUInt(packet, 5, request.FireTick);
        WriteUInt(packet, 9, request.InputEpoch);
        WriteInt(packet, 13, request.InterpDelayTicksUsed);
        int offset = 17;
        WriteVector3(packet, ref offset, request.AimDirection);
    }

    public static bool TryReadFire(ReadOnlySpan<byte> packet, out FireRequest request)
    {
        request = default;
        if (packet.Length < NetConstants.FirePacketBytes || packet[0] != (byte)PacketType.Fire)
        {
            return false;
        }

        request.FireSeq = ReadUInt(packet, 1);
        request.FireTick = ReadUInt(packet, 5);
        request.InputEpoch = ReadUInt(packet, 9);
        request.InterpDelayTicksUsed = ReadInt(packet, 13);
        int offset = 17;
        request.AimDirection = ReadVector3(packet, ref offset);
        return true;
    }

    public static void WriteFireResult(byte[] packet, in FireResult result)
    {
        packet[0] = (byte)PacketType.FireResult;
        WriteInt(packet, 1, result.ShooterPeerId);
        WriteInt(packet, 5, result.HitPeerId);
        WriteUInt(packet, 9, result.ValidatedServerTick);
    }

    public static bool TryReadFireResult(ReadOnlySpan<byte> packet, out FireResult result)
    {
        result = default;
        if (packet.Length < NetConstants.FireResultPacketBytes || packet[0] != (byte)PacketType.FireResult)
        {
            return false;
        }

        result.ShooterPeerId = ReadInt(packet, 1);
        result.HitPeerId = ReadInt(packet, 5);
        result.ValidatedServerTick = ReadUInt(packet, 9);
        return true;
    }

    public static void WriteFireVisual(byte[] packet, in FireVisual visual)
    {
        packet[0] = (byte)PacketType.FireVisual;
        WriteInt(packet, 1, visual.ShooterPeerId);
        WriteUInt(packet, 5, visual.ValidatedServerTick);
        int offset = 9;
        WriteVector3(packet, ref offset, visual.Origin);
        WriteFloat(packet, offset, visual.Yaw);
        offset += 4;
        WriteFloat(packet, offset, visual.Pitch);
        offset += 4;
        packet[offset++] = visual.DidHit ? (byte)1 : (byte)0;
        WriteVector3(packet, ref offset, visual.HitPoint);
    }

    public static bool TryReadFireVisual(ReadOnlySpan<byte> packet, out FireVisual visual)
    {
        visual = default;
        if (packet.Length < NetConstants.FireVisualPacketBytes || packet[0] != (byte)PacketType.FireVisual)
        {
            return false;
        }

        visual.ShooterPeerId = ReadInt(packet, 1);
        visual.ValidatedServerTick = ReadUInt(packet, 5);
        int offset = 9;
        visual.Origin = ReadVector3(packet, ref offset);
        visual.Yaw = ReadFloat(packet, offset);
        offset += 4;
        visual.Pitch = ReadFloat(packet, offset);
        offset += 4;
        visual.DidHit = packet[offset++] != 0;
        visual.HitPoint = ReadVector3(packet, ref offset);
        return true;
    }
}
