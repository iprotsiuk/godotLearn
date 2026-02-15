// Scripts/Net/NetCodec.Fire.cs
using Godot;

namespace NetRunnerSlice.Net;

public static partial class NetCodec
{
    public static void WriteFire(byte[] packet, in FireRequest request)
    {
        packet[0] = (byte)PacketType.Fire;
        WriteUInt(packet, 1, request.EstimatedServerTickAtFire);
        WriteUInt(packet, 5, request.InputEpoch);
        int offset = 9;
        WriteVector3(packet, ref offset, request.Origin);
        WriteFloat(packet, offset, request.Yaw);
        offset += 4;
        WriteFloat(packet, offset, request.Pitch);
    }

    public static bool TryReadFire(ReadOnlySpan<byte> packet, out FireRequest request)
    {
        request = default;
        if (packet.Length < NetConstants.FirePacketBytes || packet[0] != (byte)PacketType.Fire)
        {
            return false;
        }

        request.EstimatedServerTickAtFire = ReadUInt(packet, 1);
        request.InputEpoch = ReadUInt(packet, 5);
        int offset = 9;
        request.Origin = ReadVector3(packet, ref offset);
        request.Yaw = ReadFloat(packet, offset);
        offset += 4;
        request.Pitch = ReadFloat(packet, offset);
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
}
