// Scripts/Net/NetConstants.cs
namespace NetRunnerSlice.Net;

public static class NetChannels
{
    // Reserve channel 0 for SceneMultiplayer internals; custom transport uses 1..3.
    public const int Input = 1;
    public const int Snapshot = 2;
    public const int Control = 3;
    public const int Count = 4;
}

public static class NetConstants
{
    public const uint ProtocolVersion = 3;
    public const int MaxPlayers = 16;
    public const int MaxInputRedundancy = 3;

    public const int InputCommandBytes = 29;
    public const int SnapshotStateBytes = 41;

    public const int InputPacketBytes = 2 + (InputCommandBytes * MaxInputRedundancy);
    public const int SnapshotPacketBytes = 6 + (SnapshotStateBytes * MaxPlayers);
    public const int ControlPacketBytes = 96;
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
