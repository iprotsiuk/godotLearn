using Godot;
using NetRunnerSlice.Player.Locomotion;

namespace NetRunnerSlice.Net;

public readonly struct LocomotionNetState
{
    public readonly byte Mode;
    public readonly sbyte WallNormalX;
    public readonly sbyte WallNormalZ;
    public readonly byte WallRunTicksRemaining;
    public readonly byte SlideTicksRemaining;

    public LocomotionNetState(
        byte mode,
        sbyte wallNormalX,
        sbyte wallNormalZ,
        byte wallRunTicksRemaining,
        byte slideTicksRemaining)
    {
        Mode = mode;
        WallNormalX = wallNormalX;
        WallNormalZ = wallNormalZ;
        WallRunTicksRemaining = wallRunTicksRemaining;
        SlideTicksRemaining = slideTicksRemaining;
    }
}

public static class LocomotionNetStateCodec
{
    private const float TinyNormalLengthSq = 0.000001f;

    public static LocomotionNetState PackFrom(in LocomotionState state)
    {
        Vector2 wallXZ = new(state.WallNormal.X, state.WallNormal.Z);
        if (wallXZ.LengthSquared() > TinyNormalLengthSq)
        {
            wallXZ = wallXZ.Normalized();
        }
        else
        {
            wallXZ = Vector2.Zero;
        }

        return new LocomotionNetState(
            mode: (byte)state.Mode,
            wallNormalX: QuantizeNormalComponent(wallXZ.X),
            wallNormalZ: QuantizeNormalComponent(wallXZ.Y),
            wallRunTicksRemaining: (byte)Mathf.Clamp(state.WallRunTicksRemaining, 0, 255),
            slideTicksRemaining: (byte)Mathf.Clamp(state.SlideTicksRemaining, 0, 255));
    }

    public static LocomotionState UnpackToLocomotionState(bool groundedFallback, LocomotionNetState netState)
    {
        float nx = DequantizeNormalComponent(netState.WallNormalX);
        float nz = DequantizeNormalComponent(netState.WallNormalZ);

        Vector3 wallNormal = new(nx, 0.0f, nz);
        if (wallNormal.LengthSquared() <= TinyNormalLengthSq)
        {
            wallNormal = Vector3.Zero;
        }
        else
        {
            wallNormal = wallNormal.Normalized();
            wallNormal.Y = 0.0f;
        }

        LocomotionMode mode = DecodeMode(netState.Mode, groundedFallback);

        return new LocomotionState
        {
            Mode = mode,
            ModeTicks = 0,
            WallNormal = wallNormal,
            WallRunTicksRemaining = netState.WallRunTicksRemaining,
            SlideTicksRemaining = netState.SlideTicksRemaining
        };
    }

    public static sbyte QuantizeNormalComponent(float value)
    {
        float clamped = Mathf.Clamp(value, -1.0f, 1.0f);
        int quantized = Mathf.RoundToInt(clamped * 127.0f);
        quantized = Mathf.Clamp(quantized, -127, 127);
        return (sbyte)quantized;
    }

    public static float DequantizeNormalComponent(sbyte value)
    {
        float decoded = value / 127.0f;
        return Mathf.Clamp(decoded, -1.0f, 1.0f);
    }

    private static LocomotionMode DecodeMode(byte mode, bool groundedFallback)
    {
        if (mode <= (byte)LocomotionMode.Slide)
        {
            return (LocomotionMode)mode;
        }

        return groundedFallback ? LocomotionMode.Grounded : LocomotionMode.Air;
    }
}
