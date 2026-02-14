// Scripts/Net/NetCodec.Control.cs
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
        int serverTickRate,
        int clientTickRate,
        int snapshotRate,
        int interpolationDelayMs,
        int maxExtrapolationMs,
        int reconcileSmoothMs,
        float reconcileSnapThreshold,
        float pitchClampDegrees)
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

    public static ushort ReadControlPingSeq(ReadOnlySpan<byte> packet) => ReadUShort(packet, 2);

    public static uint ReadControlClientTime(ReadOnlySpan<byte> packet) => ReadUInt(packet, 4);

    public static uint ReadControlServerTick(ReadOnlySpan<byte> packet) => ReadUInt(packet, 8);
}
