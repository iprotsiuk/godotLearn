using System.Collections.Generic;
using Godot;
using NetRunnerSlice.Player.Locomotion;

namespace NetRunnerSlice.Net;

public partial class NetSession
{
    private const double FocusOutResetGraceSec = 0.35;

    private const int FirePressDiagCapacity = 128;

    private struct FirePressDiagSample
    {
        public long LocalUsec;
        public int AppliedDelayTicks;
        public int TargetDelayTicks;
        public float RttMs;
        public float JitterMs;
        public uint ClientEstServerTick;
        public uint ClientSendTick;
        public int HorizonGap;
    }

    private struct InputState
    {
        public Vector2 MoveAxes;
        public bool JumpHeld;
    }

    private readonly Dictionary<uint, FirePressDiagSample> _firePressDiagByTick = new();
    private readonly Queue<uint> _firePressDiagOrder = new();
    private InputState _inputState;
    private int _jumpPressRepeatTicksRemaining;
    private int _firePressRepeatTicksRemaining;
    private int _interactPressRepeatTicksRemaining;

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
        ProcessDeferredFocusOutReset();
        if (!_hasFocus || IsLocalFrozenAtTick(_mode == RunMode.ListenServer ? _server_sim_tick : GetEstimatedServerTickNow()))
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
        if (_localCharacter is null || IsLocalFrozenAtTick(_mode == RunMode.ListenServer ? _server_sim_tick : GetEstimatedServerTickNow()))
        {
            return;
        }

        bool allowJumpPress =
            _localCharacter.Grounded ||
            _localCharacter.CurrentLocomotionMode == LocomotionMode.WallRun ||
            _localCharacter.CurrentLocomotionMode == LocomotionMode.WallCling;
        if (!allowJumpPress)
        {
            return;
        }

        _jumpPressRepeatTicksRemaining = Mathf.Clamp(2, 1, NetConstants.MaxInputRedundancy);
    }

    private void TryLatchFirePressed()
    {
        if (IsLocalFrozenAtTick(_mode == RunMode.ListenServer ? _server_sim_tick : GetEstimatedServerTickNow()))
        {
            return;
        }

        if (GetLocalEquippedItemForClientView() == NetRunnerSlice.Items.ItemId.None)
        {
            return;
        }

        uint nowTick = _mode == RunMode.ListenServer ? _server_sim_tick : GetEstimatedServerTickNow();
        if (IsLocalWeaponCoolingDownAtTick(nowTick))
        {
            return;
        }

        _firePressRepeatTicksRemaining = Mathf.Clamp(1, 1, NetConstants.MaxInputRedundancy);
        RecordLocalFirePressDiag(_client_send_tick);
        TrySpawnPredictedLocalFireVisual();
    }

    private void TryLatchInteractPressed()
    {
        if (IsLocalFrozenAtTick(_mode == RunMode.ListenServer ? _server_sim_tick : GetEstimatedServerTickNow()))
        {
            return;
        }

        _interactPressRepeatTicksRemaining = Mathf.Clamp(1, 1, NetConstants.MaxInputRedundancy);
    }

    private void OnFocusOut()
    {
        LogFocusDiag("out");
        _hasFocus = false;
        _focusOutPending = true;
        _focusOutResetApplied = false;
        _focusOutStartedAtSec = Time.GetTicksMsec() / 1000.0;
        _inputState = default;
    }

    private void OnFocusIn()
    {
        LogFocusDiag("in");
        _hasFocus = true;
        double nowSec = Time.GetTicksMsec() / 1000.0;
        bool transientFocusBlip = _focusOutPending &&
            !_focusOutResetApplied &&
            (nowSec - _focusOutStartedAtSec) < FocusOutResetGraceSec;
        _focusOutPending = false;
        _inputState = default;

        if (transientFocusBlip)
        {
            Input.FlushBufferedEvents();
            return;
        }

        AdvanceInputEpoch(resetTickToServerEstimate: true);
        ReleaseGameplayActions();
        Input.FlushBufferedEvents();
    }

    private void ProcessDeferredFocusOutReset()
    {
        if (!_focusOutPending || _focusOutResetApplied || _hasFocus)
        {
            return;
        }

        double nowSec = Time.GetTicksMsec() / 1000.0;
        if ((nowSec - _focusOutStartedAtSec) < FocusOutResetGraceSec)
        {
            return;
        }

        _focusOutResetApplied = true;
        AdvanceInputEpoch(resetTickToServerEstimate: false);
        ReleaseGameplayActions();
        Input.FlushBufferedEvents();
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private void AdvanceInputEpoch(bool resetTickToServerEstimate)
    {
        _inputEpoch++;
        if (_inputEpoch == 0)
        {
            _inputEpoch = 1;
        }

        _pendingInputs.Clear();
        _pingSent.Clear();
        ClearLocalFirePressDiag();
        _lastAckedSeq = _nextInputSeq;
        _jumpPressRepeatTicksRemaining = 0;
        _firePressRepeatTicksRemaining = 0;
        _interactPressRepeatTicksRemaining = 0;
        _localCharacter?.ClearRenderCorrection();
        _localCharacter?.ClearViewCorrection();

        if (resetTickToServerEstimate && IsClient)
        {
            RebaseClientTickToServerEstimate();
        }

        _nextPingTimeSec = (Time.GetTicksMsec() / 1000.0) + 0.1;
    }

    private static void ReleaseGameplayActions()
    {
        Input.ActionRelease("move_forward");
        Input.ActionRelease("move_back");
        Input.ActionRelease("move_left");
        Input.ActionRelease("move_right");
        Input.ActionRelease("jump");
        Input.ActionRelease("fire");
        Input.ActionRelease("interact");
    }

    private void RecordLocalFirePressDiag(uint fireTick)
    {
        if (!IsClient || fireTick == 0)
        {
            return;
        }

        uint desiredHorizonTick = GetDesiredHorizonTick();
        FirePressDiagSample sample = new()
        {
            LocalUsec = (long)Time.GetTicksUsec(),
            AppliedDelayTicks = _appliedInputDelayTicks,
            TargetDelayTicks = _targetInputDelayTicks,
            RttMs = _rttMs,
            JitterMs = _jitterMs,
            ClientEstServerTick = _client_est_server_tick,
            ClientSendTick = _client_send_tick,
            HorizonGap = (int)desiredHorizonTick - (int)_client_send_tick
        };

        if (!_firePressDiagByTick.ContainsKey(fireTick))
        {
            _firePressDiagOrder.Enqueue(fireTick);
        }

        _firePressDiagByTick[fireTick] = sample;
        while (_firePressDiagOrder.Count > FirePressDiagCapacity)
        {
            uint oldestTick = _firePressDiagOrder.Dequeue();
            _firePressDiagByTick.Remove(oldestTick);
        }
    }

    private bool TryConsumeLocalFirePressDiag(uint fireTick, out FirePressDiagSample sample)
    {
        if (_firePressDiagByTick.TryGetValue(fireTick, out sample))
        {
            _firePressDiagByTick.Remove(fireTick);
            return true;
        }

        sample = default;
        return false;
    }

    private void ClearLocalFirePressDiag()
    {
        _firePressDiagByTick.Clear();
        _firePressDiagOrder.Clear();
    }

    private void LogFocusDiag(string direction)
    {
        uint estimatedTick = _mode == RunMode.Client
            ? GetEstimatedServerTickNow()
            : _server_sim_tick;
        GD.Print(
            $"FocusDiag: {direction} epoch={_inputEpoch} client_send_tick={_client_send_tick} " +
            $"est_server_tick={estimatedTick} appliedDelayTicks={_appliedInputDelayTicks} " +
            $"rtt/jitter={_rttMs:0.0}/{_jitterMs:0.0}ms");
    }
}
