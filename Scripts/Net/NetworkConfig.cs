// Scripts/Net/NetworkConfig.cs
namespace NetRunnerSlice.Net;

public sealed class NetworkConfig
{
    public int ServerTickRate { get; set; } = 60;
    public int ClientTickRate { get; set; } = 60;
    public int SnapshotRate { get; set; } = 20;
    public int InterpolationDelayMs { get; set; } = 100;
    public int MaxExtrapolationMs { get; set; } = 100;
    public float ReconciliationSnapThreshold { get; set; } = 1.5f;
    public int ReconciliationSmoothMs { get; set; } = 100;
    public int MaxPlayers { get; set; } = 16;

    public float MoveSpeed { get; set; } = 9.0f;
    public float GroundAcceleration { get; set; } = 45.0f;
    public float AirAcceleration { get; set; } = 14.0f;
    public float AirControlFactor { get; set; } = 0.35f;
    public float JumpVelocity { get; set; } = 8.75f;
    public float Gravity { get; set; } = 24.0f;

    public float MouseSensitivity { get; set; } = 0.0023f;
    public float PitchClampDegrees { get; set; } = 89.0f;

    public int SimulatedLatencyMs { get; set; } = 0;
    public int SimulatedJitterMs { get; set; } = 0;
    public float SimulatedLossPercent { get; set; } = 0.0f;
    public int SimulationSeed { get; set; } = 1337;
}
