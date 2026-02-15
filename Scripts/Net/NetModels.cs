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
    public uint InputTick;
    public uint InputEpoch;
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
    public uint DroppedOldInputCount;
    public uint DroppedFutureInputCount;
    public uint TicksUsedBufferedInput;
    public uint TicksUsedHoldLast;
    public uint TicksUsedNeutral;
    public uint MissingInputStreakCurrent;
    public uint MissingInputStreakMax;
    public int EffectiveDelayTicks;
    public float ServerPeerRttMs;
    public float ServerPeerJitterMs;
}

public struct RemoteSample
{
    public Vector3 Pos;
    public Vector3 Vel;
    public float Yaw;
    public float Pitch;
    public bool Grounded;
}

public struct FireRequest
{
    public uint EstimatedServerTickAtFire;
    public uint InputEpoch;
    public Vector3 Origin;
    public float Yaw;
    public float Pitch;
}

public struct FireResult
{
    public int ShooterPeerId;
    public int HitPeerId;
    public uint ValidatedServerTick;
}

public struct FireVisual
{
    public int ShooterPeerId;
    public uint ValidatedServerTick;
    public Vector3 Origin;
    public float Yaw;
    public float Pitch;
    public bool DidHit;
    public Vector3 HitPoint;
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
    public float DynamicInterpolationDelayMs;
    public float SessionJitterEstimateMs;
    public uint ServerDroppedOldInputCount;
    public uint ServerDroppedFutureInputCount;
    public uint ServerTicksUsedBufferedInput;
    public uint ServerTicksUsedHoldLast;
    public uint ServerTicksUsedNeutral;
    public uint ServerMissingInputStreakCurrent;
    public uint ServerMissingInputStreakMax;
    public int ServerEffectiveDelayTicks;
    public float ServerPeerRttMs;
    public float ServerPeerJitterMs;
}
