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
    public const uint ProtocolVersion = 5;
    public const int MaxPlayers = 16;
    public const int MaxInputRedundancy = 5;

    public const int InputCommandBytes = 33;
    public const int SnapshotStateBytes = 81;
    public const int FirePacketBytes = 29;
    public const int FireResultPacketBytes = 13;
    public const int FireVisualPacketBytes = 42;

    public const int InputPacketBytes = 2 + (InputCommandBytes * MaxInputRedundancy);
    public const int SnapshotPacketBytes = 6 + (SnapshotStateBytes * MaxPlayers);
    public const int ControlPacketBytes = 96;

    public const int MinWanInputDelayTicks = 2;
    public const int MaxWanInputDelayTicks = 10;
    public const float WanInputSafetyMs = 20.0f;
    public const float WanInputJitterScale = 1.0f;
    public const double InputDelayUpdateIntervalSec = 0.25;
    public const float WanDefaultRttMs = 100.0f;
    public const int MaxFutureInputTicks = 64;
    public const int MaxPastInputTicks = 32;
    public const int HoldLastInputTicks = 2;
    public const int MaxMissingBeforeResyncHint = 120;
    public const int PendingInputHardCap = 256;
    public const float StallEpochThresholdSeconds = 0.2f;
    public const double PingIntervalSec = 0.5;
    public const float RttEwmaAlpha = 0.2f;
    public const int MaxOutstandingPings = 128;
}

public enum PacketType : byte
{
    InputBundle = 1,
    Snapshot = 2,
    Control = 3,
    Fire = 4,
    FireResult = 5,
    FireVisual = 6
}

public enum ControlType : byte
{
    Hello = 1,
    Welcome = 2,
    Ping = 3,
    Pong = 4,
    DelayUpdate = 5,
    ResyncHint = 6
}
