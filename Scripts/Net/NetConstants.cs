// Scripts/Net/NetConstants.cs
namespace NetRunnerSlice.Net;

public static class NetChannels
{
    public const int Input = 0;
    public const int Snapshot = 1;
    public const int Control = 2;
    public const int Count = 3;
}

public static class NetConstants
{
    public const uint ProtocolVersion = 2;
    public const int MaxPlayers = 16;
    public const int MaxInputRedundancy = 3;

    public const int InputCommandBytes = 29;
    public const int SnapshotStateBytes = 41;

    public const int InputPacketBytes = 2 + (InputCommandBytes * MaxInputRedundancy);
    public const int SnapshotPacketBytes = 6 + (SnapshotStateBytes * MaxPlayers);
    public const int ControlPacketBytes = 48;
}

public enum PacketType : byte
{
    InputBundle = 1,
    Snapshot = 2,
    Control = 3
}

public enum ControlType : byte
{
    Hello = 1,
    Welcome = 2,
    Ping = 3,
    Pong = 4
}
