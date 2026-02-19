// Scripts/Net/NetworkConfig.cs
using Godot;

namespace NetRunnerSlice.Net;

public sealed class NetworkConfig
{
    public int ServerTickRate { get; set; } = 60;
    public int ClientTickRate { get; set; } = 60;
    public int SnapshotRate { get; set; } = 20;
    public int ServerInputDelayTicks { get; set; } = 3;
    public int InterpolationDelayMs { get; set; } = 100;
    public int MaxExtrapolationMs { get; set; } = 100;
    public bool UseHermiteInterpolation { get; set; } = false;
    public float ReconciliationSnapThreshold { get; set; } = 1.5f;
    public int ReconciliationSmoothMs { get; set; } = 100;
    public int MaxPlayers { get; set; } = 16;

    public float MoveSpeed { get; set; } = 9.0f;
    public float GroundAcceleration { get; set; } = 45.0f;
    public float AirAcceleration { get; set; } = 14.0f;
    public float AirControlFactor { get; set; } = 0.35f;
    public float JumpVelocity { get; set; } = 8.75f;
    public float Gravity { get; set; } = 24.0f;
    public float FloorSnapLength { get; set; } = 0.25f;
    public float GroundStickVelocity { get; set; } = -0.1f;
    // 0 disables wallrun timeout (run persists while wall contact remains valid).
    public int WallRunMaxTicks { get; set; } = 0;
    public int SlideMaxTicks { get; set; } = 0;
    public float WallRunGravityScale { get; set; } = 0.35f;
    public float WallJumpUpVelocity { get; set; } = 8.75f;
    public float WallJumpAwayVelocity { get; set; } = 6.0f;

    public float MouseSensitivity { get; set; } = 0.0023f;
    public bool InvertLookY { get; set; } = false;
    public float LocalFov { get; set; } = 90.0f;
    public float PitchClampDegrees { get; set; } = 89.0f;
    public bool AllowInputWhenUnfocused { get; set; } = true;
    public bool KeepNetworkingWhenUnfocused { get; set; } = OS.HasFeature("windows") || OS.HasFeature("macos") || OS.HasFeature("linuxbsd");
    public bool EnableFrameHitchDiagnostics { get; set; } = true;
    public float PhysicsHitchThresholdMs { get; set; } = 35.0f;
    public float ProcessHitchThresholdMs { get; set; } = 45.0f;

    public int SimulatedLatencyMs { get; set; } = 0;
    public int SimulatedJitterMs { get; set; } = 0;
    public float SimulatedLossPercent { get; set; } = 0.0f;
    public int SimulationSeed { get; set; } = 1337;
}
