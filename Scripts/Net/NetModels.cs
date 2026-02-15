// Scripts/Net/NetModels.cs
using Godot;

namespace NetRunnerSlice.Net;

[System.Flags]
public enum InputButtons : byte
{
    None = 0,
    JumpPressed = 1 << 0,
    JumpHeld = 1 << 1
}

public struct InputCommand
{
    public uint Seq;
    public uint ClientTick;
    public float DtFixed;
    public Vector2 MoveAxes;
    public InputButtons Buttons;
    public float Yaw;
    public float Pitch;
}

public struct PlayerStateSnapshot
{
    public int PeerId;
    public uint LastProcessedSeqForThatClient;
    public Vector3 Pos;
    public Vector3 Vel;
    public float Yaw;
    public float Pitch;
    public bool Grounded;
}

public struct RemoteSample
{
    public Vector3 Pos;
    public Vector3 Vel;
    public float Yaw;
    public float Pitch;
    public bool Grounded;
}

public struct SessionMetrics
{
    public uint ServerTick;
    public uint ClientTick;
    public uint LastAckedInput;
    public int PendingInputCount;
    public int JumpRepeatRemaining;
    public float LastCorrectionMagnitude;
    public float CorrXZ;
    public float CorrY;
    public float Corr3D;
    public float RttMs;
    public float JitterMs;
    public bool LocalGrounded;
    public float MoveSpeed;
    public float GroundAcceleration;
    public int ServerInputDelayTicks;
    public bool NetworkSimulationEnabled;
    public int SimLatencyMs;
    public int SimJitterMs;
    public float SimLossPercent;
}
