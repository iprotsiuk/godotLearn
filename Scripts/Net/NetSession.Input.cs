using Godot;

namespace NetRunnerSlice.Net;

public partial class NetSession
{
    private struct InputState
    {
        public Vector2 MoveAxes;
        public bool JumpHeld;
    }

    private InputState _inputState;
    private int _jumpPressRepeatTicksRemaining;

    private void CaptureInputState()
    {
        _inputState.MoveAxes = new Vector2(
            Input.GetActionStrength("move_right") - Input.GetActionStrength("move_left"),
            Input.GetActionStrength("move_forward") - Input.GetActionStrength("move_back"));
        _inputState.JumpHeld = Input.IsActionPressed("jump");
    }

    private void TryLatchGroundedJump()
    {
        if (_localCharacter is null || !_localCharacter.Grounded)
        {
            return;
        }

        _jumpPressRepeatTicksRemaining = Mathf.Clamp(2, 1, NetConstants.MaxInputRedundancy);
    }
}
