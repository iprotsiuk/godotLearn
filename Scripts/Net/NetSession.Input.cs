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
    private int _firePressRepeatTicksRemaining;

    public override void _Notification(int what)
    {
        if (what == MainLoop.NotificationApplicationFocusOut)
        {
            OnFocusOut();
        }
        else if (what == MainLoop.NotificationApplicationFocusIn)
        {
            OnFocusIn();
        }
    }

    private void CaptureInputState()
    {
        if (!_hasFocus)
        {
            _inputState.MoveAxes = Vector2.Zero;
            _inputState.JumpHeld = false;
            return;
        }

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

    private void TryLatchFirePressed()
    {
        _firePressRepeatTicksRemaining = Mathf.Clamp(1, 1, NetConstants.MaxInputRedundancy);
        TrySpawnPredictedLocalFireVisual();
    }

    private void OnFocusOut()
    {
        _hasFocus = false;
        _inputState = default;
        AdvanceInputEpoch(resetTickToServerEstimate: false);
        ReleaseGameplayActions();
        Input.FlushBufferedEvents();
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private void OnFocusIn()
    {
        _hasFocus = true;
        _inputState = default;
        AdvanceInputEpoch(resetTickToServerEstimate: true);
        ReleaseGameplayActions();
        Input.FlushBufferedEvents();
    }

    private void AdvanceInputEpoch(bool resetTickToServerEstimate)
    {
        _inputEpoch++;
        if (_inputEpoch == 0)
        {
            _inputEpoch = 1;
        }

        _pendingInputs.Clear();
        _lastAckedSeq = _nextInputSeq;
        _jumpPressRepeatTicksRemaining = 0;
        _firePressRepeatTicksRemaining = 0;
        _localCharacter?.ClearRenderCorrection();
        _localCharacter?.ClearViewCorrection();

        if (resetTickToServerEstimate && IsClient)
        {
            RebaseClientTickToServerEstimate();
        }
    }

    private static void ReleaseGameplayActions()
    {
        Input.ActionRelease("move_forward");
        Input.ActionRelease("move_back");
        Input.ActionRelease("move_left");
        Input.ActionRelease("move_right");
        Input.ActionRelease("jump");
        Input.ActionRelease("fire");
    }
}
