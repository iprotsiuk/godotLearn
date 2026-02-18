// Scripts/Net/InputSanitizer.cs
using Godot;

namespace NetRunnerSlice.Net;

public static class InputSanitizer
{
    private const InputButtons AllowedButtons =
        InputButtons.JumpHeld |
        InputButtons.JumpPressed |
        InputButtons.FirePressed |
        InputButtons.InteractPressed;

    public static bool TrySanitizeServer(ref InputCommand command, NetworkConfig config)
    {
        if (command.Seq == 0 || command.InputTick == 0 || command.InputEpoch == 0)
        {
            return false;
        }

        if (!IsFinite(command.MoveAxes.X) || !IsFinite(command.MoveAxes.Y) ||
            !IsFinite(command.Yaw) || !IsFinite(command.Pitch))
        {
            return false;
        }

        command.Buttons &= AllowedButtons;

        Vector2 move = command.MoveAxes;
        move.X = Mathf.Clamp(move.X, -1.0f, 1.0f);
        move.Y = Mathf.Clamp(move.Y, -1.0f, 1.0f);
        if (move.LengthSquared() > 1.0f)
        {
            move = move.Normalized();
        }

        float pitchClamp = Mathf.DegToRad(config.PitchClampDegrees);
        command.MoveAxes = move;
        command.Yaw = Mathf.Wrap(command.Yaw, -Mathf.Pi, Mathf.Pi);
        command.Pitch = Mathf.Clamp(command.Pitch, -pitchClamp, pitchClamp);
        command.DtFixed = 1.0f / config.ServerTickRate;
        return true;
    }

    public static void SanitizeClient(ref InputCommand command, NetworkConfig config)
    {
        Vector2 move = command.MoveAxes;
        move.X = Mathf.Clamp(move.X, -1.0f, 1.0f);
        move.Y = Mathf.Clamp(move.Y, -1.0f, 1.0f);
        if (move.LengthSquared() > 1.0f)
        {
            move = move.Normalized();
        }

        float pitchClamp = Mathf.DegToRad(config.PitchClampDegrees);
        command.MoveAxes = move;
        command.Buttons &= AllowedButtons;
        if (command.InputEpoch == 0)
        {
            command.InputEpoch = 1;
        }

        command.Yaw = IsFinite(command.Yaw) ? Mathf.Wrap(command.Yaw, -Mathf.Pi, Mathf.Pi) : 0.0f;
        command.Pitch = IsFinite(command.Pitch)
            ? Mathf.Clamp(command.Pitch, -pitchClamp, pitchClamp)
            : 0.0f;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
