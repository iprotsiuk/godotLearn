// Scripts/Net/NetSession.Client.cs
using System.Collections.Generic;
using Godot;
using NetRunnerSlice.Player;

namespace NetRunnerSlice.Net;

public partial class NetSession
{
    private const float GlobalInterpJitterScale = 1.0f;
    private const float GlobalInterpMaxExtraMs = 100.0f;
    private const float SessionJitterEwmaAlpha = 0.1f;
    private const float MaxInterpHoldOrExtrapMs = 100.0f;
    private const float InterpGapJumpMeters = 2.5f;

    private void ClientConnectedToServer()
    {
        if (_mode != RunMode.Client)
        {
            return;
        }

        NetCodec.WriteControlHello(_controlPacket);
        SendPacket(1, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
        GD.Print("NetSession: Hello sent");
        _nextPingTimeSec = (Time.GetTicksMsec() / 1000.0) + 0.1;
    }

    private void TickClient(float delta)
    {
        if (delta > NetConstants.StallEpochThresholdSeconds)
        {
            AdvanceInputEpoch(resetTickToServerEstimate: true);
        }

        _client_est_server_tick = GetEstimatedServerTickNow();
        MaybeResyncClient("tick_drift_guard");
        UpdateAppliedInputDelayTicks();

        uint desired_horizon_tick = GetDesiredHorizonTick();
        int sentThisTick = SendInputsUpToDesiredHorizon(desired_horizon_tick, allowPrediction: _localCharacter is not null);
        _clientInputCmdsSentSinceLastDiag += sentThisTick;
        LogClientJoinDiagnosticsIfDue(desired_horizon_tick);

        if (_mode != RunMode.Client)
        {
            return;
        }

        double nowSec = Time.GetTicksMsec() / 1000.0;
        if (nowSec >= _nextPingTimeSec)
        {
            _pingSeq++;
            uint nowMs = (uint)Time.GetTicksMsec();
            _pingSent[_pingSeq] = nowSec;
            if (_pingSent.Count > NetConstants.MaxOutstandingPings)
            {
                ushort oldestSeq = 0;
                bool found = false;
                foreach (ushort seq in _pingSent.Keys)
                {
                    oldestSeq = seq;
                    found = true;
                    break;
                }

                if (found)
                {
                    _pingSent.Remove(oldestSeq);
                }
            }

            NetCodec.WriteControlPing(_controlPacket, _pingSeq, nowMs);
            SendPacket(1, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
            _nextPingTimeSec = nowSec + NetConstants.PingIntervalSec;
        }
    }

    private int GetClientInputDelayTicksForStamping()
    {
        if (_mode == RunMode.ListenServer && _localPeerId != 0)
        {
            return 0;
        }

        return Mathf.Max(0, _appliedInputDelayTicks);
    }

    private uint GetDesiredHorizonTick()
    {
        uint delayTicks = (uint)GetClientInputDelayTicksForStamping();
        uint safetyTicks = (uint)Mathf.Max(0, ClientInputSafetyTicks);
        return _client_est_server_tick + delayTicks + safetyTicks;
    }

    private void RebaseClientTickToServerEstimate()
    {
        _client_est_server_tick = _mode == RunMode.ListenServer
            ? _server_sim_tick
            : GetEstimatedServerTickNow();

        uint minSendTick = _client_est_server_tick + 1;
        if (_client_send_tick == 0)
        {
            _client_send_tick = minSendTick;
        }
    }

    private int SendInputsUpToDesiredHorizon(uint desired_horizon_tick, bool allowPrediction)
    {
        if (!IsClient)
        {
            return 0;
        }

        uint minSendTick = _client_est_server_tick + 1;
        if (_client_send_tick == 0)
        {
            _client_send_tick = minSendTick;
        }

        int generatedCount = 0;
        int packetCount = 0;
        while (_client_send_tick <= desired_horizon_tick)
        {
            int count = 0;
            while (_client_send_tick <= desired_horizon_tick && count < NetConstants.MaxInputRedundancy)
            {
                InputCommand command = BuildInputCommandForTick(_client_send_tick);
                _inputSendScratch[count++] = command;
                _pendingInputs.Add(command);
                if (_pendingInputs.Count >= NetConstants.PendingInputHardCap)
                {
                    TriggerClientResync("pending_cap_guard", _lastAuthoritativeServerTick);
                    return generatedCount;
                }

                if (allowPrediction && _localCharacter is not null)
                {
                    _localCharacter.SetLook(command.Yaw, command.Pitch);
                    PlayerMotor.Simulate(_localCharacter, command, _config);
                }

                _client_send_tick++;
                generatedCount++;
            }

            if (count <= 0)
            {
                break;
            }

            NetCodec.WriteInputBundle(_inputPacket, _inputSendScratch.AsSpan(0, count));
            if (_mode == RunMode.ListenServer)
            {
                HandleInputBundle(_localPeerId, _inputPacket);
            }
            else
            {
                SendPacket(1, NetChannels.Input, MultiplayerPeer.TransferModeEnum.Unreliable, _inputPacket);
            }

            packetCount++;
            if (packetCount > 64)
            {
                break;
            }
        }

        return generatedCount;
    }

    private InputCommand BuildInputCommandForTick(uint sendTick)
    {
        InputButtons buttons = ConsumeInputButtons();
        InputCommand command = new()
        {
            Seq = ++_nextInputSeq,
            InputTick = sendTick,
            InputEpoch = _inputEpoch,
            DtFixed = 1.0f / _config.ClientTickRate,
            MoveAxes = _inputState.MoveAxes,
            Buttons = buttons,
            Yaw = _lookYaw,
            Pitch = _lookPitch
        };
        InputSanitizer.SanitizeClient(ref command, _config);
        return command;
    }

    private InputButtons ConsumeInputButtons()
    {
        bool jumpPressed = _jumpPressRepeatTicksRemaining > 0;
        if (_jumpPressRepeatTicksRemaining > 0)
        {
            _jumpPressRepeatTicksRemaining--;
        }

        InputButtons buttons = InputButtons.None;
        if (_inputState.JumpHeld)
        {
            buttons |= InputButtons.JumpHeld;
        }

        if (jumpPressed)
        {
            buttons |= InputButtons.JumpPressed;
        }

        bool firePressed = _firePressRepeatTicksRemaining > 0;
        if (_firePressRepeatTicksRemaining > 0)
        {
            _firePressRepeatTicksRemaining--;
        }

        if (firePressed)
        {
            buttons |= InputButtons.FirePressed;
        }

        return buttons;
    }

    private void LogClientJoinDiagnosticsIfDue(uint desired_horizon_tick)
    {
        if (_mode != RunMode.Client)
        {
            return;
        }

        double nowSec = Time.GetTicksMsec() / 1000.0;
        if (nowSec > _clientJoinDiagUntilSec || nowSec < _clientNextJoinDiagAtSec)
        {
            return;
        }

        int horizonGap = (int)desired_horizon_tick - (int)_client_send_tick;
        GD.Print(
            $"ClientJoinDiag: local_time_ms={Time.GetTicksMsec()} client_est_server_tick={_client_est_server_tick} " +
            $"input_delay_ticks={_appliedInputDelayTicks} safety_ticks={ClientInputSafetyTicks} " +
            $"client_send_tick={_client_send_tick} desired_horizon_tick={desired_horizon_tick} horizon_gap={horizonGap} " +
            $"sent_since_last={_clientInputCmdsSentSinceLastDiag}");
        _clientInputCmdsSentSinceLastDiag = 0;
        _clientNextJoinDiagAtSec = nowSec + 0.1;
    }

    private void HandleSnapshot(byte[] packet)
    {
        if (!IsClient)
        {
            return;
        }

        if (_mode == RunMode.Client && _localPeerId == 0)
        {
            return;
        }

        if (!NetCodec.TryReadSnapshot(packet, out uint serverTick, _snapshotDecodeScratch, out int count))
        {
            return;
        }

        long localNowUsec = GetLocalUsec();
        double localNowSec = localNowUsec / 1_000_000.0;
        ObserveAuthoritativeServerTick(serverTick, localNowUsec);
        _server_sim_tick = serverTick;
        _lastAuthoritativeServerTick = serverTick;

        UpdateSessionSnapshotJitter(localNowSec);

        uint snapshotServerTick = serverTick;
        for (int i = 0; i < count; i++)
        {
            PlayerStateSnapshot state = _snapshotDecodeScratch[i];
            if (state.PeerId == _localPeerId)
            {
                ReconcileLocal(state);
                continue;
            }

            if (!_remotePlayers.TryGetValue(state.PeerId, out RemoteEntity? remote))
            {
                PlayerCharacter character = CreateCharacter(state.PeerId, false);
                remote = new RemoteEntity { Character = character };
                _remotePlayers[state.PeerId] = remote;
            }

            double expectedArrivalDeltaSec = 1.0 / Mathf.Max(1, _config.SnapshotRate);
            remote.Buffer.ObserveArrival(localNowSec, expectedArrivalDeltaSec);

            RemoteSample sample = new()
            {
                Pos = state.Pos,
                Vel = state.Vel,
                Yaw = state.Yaw,
                Pitch = state.Pitch,
                Grounded = state.Grounded
            };
            remote.Buffer.Add(snapshotServerTick, sample);
        }
    }

    private void ReconcileLocal(in PlayerStateSnapshot snapshot)
    {
        if (_localCharacter is null)
        {
            return;
        }

        _serverDroppedOldInputCount = snapshot.DroppedOldInputCount;
        _serverDroppedFutureInputCount = snapshot.DroppedFutureInputCount;
        _serverTicksUsedBufferedInput = snapshot.TicksUsedBufferedInput;
        _serverTicksUsedHoldLast = snapshot.TicksUsedHoldLast;
        _serverTicksUsedNeutral = snapshot.TicksUsedNeutral;
        _serverMissingInputStreakCurrent = snapshot.MissingInputStreakCurrent;
        _serverMissingInputStreakMax = snapshot.MissingInputStreakMax;
        _serverEffectiveDelayTicks = snapshot.EffectiveDelayTicks;
        _serverPeerRttMs = snapshot.ServerPeerRttMs;
        _serverPeerJitterMs = snapshot.ServerPeerJitterMs;

        Vector3 before = _localCharacter.GlobalPosition;
        _localCharacter.GlobalPosition = snapshot.Pos;
        _localCharacter.Velocity = snapshot.Vel;
        _localCharacter.SetGroundedOverride(snapshot.Grounded);

        if (snapshot.LastProcessedSeqForThatClient > _lastAckedSeq)
        {
            _lastAckedSeq = snapshot.LastProcessedSeqForThatClient;
        }
        if (_lastAckedSeq > _nextInputSeq)
        {
            _lastAckedSeq = _nextInputSeq;
        }
        _pendingInputs.RemoveUpTo(_lastAckedSeq);

        for (uint seq = _lastAckedSeq + 1; seq <= _nextInputSeq; seq++)
        {
            if (_pendingInputs.TryGet(seq, out InputCommand replay))
            {
                _localCharacter.SetLook(replay.Yaw, replay.Pitch);
                PlayerMotor.Simulate(_localCharacter, replay, _config);
            }
        }

        _localCharacter.SetLook(_lookYaw, _lookPitch);
        Vector3 after = _localCharacter.GlobalPosition;
        Vector3 correctionDelta = before - after;
        float corrXZ = new Vector2(correctionDelta.X, correctionDelta.Z).Length();
        float corrY = Mathf.Abs(correctionDelta.Y);
        float corr3D = correctionDelta.Length();
        _lastCorrectionXZMeters = corrXZ;
        _lastCorrectionYMeters = corrY;
        _lastCorrection3DMeters = corr3D;
        _lastCorrectionMeters = corr3D;

        Vector3 renderOffset = new(correctionDelta.X, 0.0f, correctionDelta.Z);
        if (corrXZ > _config.ReconciliationSnapThreshold)
        {
            _localCharacter.ClearRenderCorrection();
        }
        else
        {
            _localCharacter.AddRenderCorrection(renderOffset, _config.ReconciliationSmoothMs);
        }

        Vector3 viewOffset = new(0.0f, correctionDelta.Y, 0.0f);
        if (corrY > 0.5f)
        {
            _localCharacter.ClearViewCorrection();
        }
        else
        {
            int viewSmoothMs = Mathf.Min(40, _config.ReconciliationSmoothMs);
            _localCharacter.AddViewCorrection(viewOffset, viewSmoothMs);
        }
    }

    private void UpdateRemoteInterpolation()
    {
        if (!IsClient || _netClock is null)
        {
            return;
        }

        long nowUsec = GetLocalUsec();
        double nowSec = nowUsec / 1_000_000.0;
        uint estimatedServerTickNow = GetEstimatedServerTickNow();
        int baseInterpDelayTicks = MsToTicks(Mathf.Max(0.0f, _config.InterpolationDelayMs));
        int jitterExtraTicks = MsToTicks(Mathf.Clamp(
            GlobalInterpJitterScale * _sessionSnapshotJitterEwmaMs,
            0.0f,
            GlobalInterpMaxExtraMs));
        int targetInterpDelayTicks = Mathf.Clamp(
            baseInterpDelayTicks + jitterExtraTicks + _interpUnderflowExtraTicks,
            0,
            baseInterpDelayTicks + MsToTicks(GlobalInterpMaxExtraMs) + 32);
        UpdateGlobalInterpDelayTicks(targetInterpDelayTicks, nowSec);
        // Tick clock: client_render_tick (visual interpolation only, never used for gameplay or input stamping).
        double client_render_tick = estimatedServerTickNow - _globalInterpDelayTicks;
        double maxExtrapTicks = MsToTicks(Mathf.Min(_config.MaxExtrapolationMs, (int)MaxInterpHoldOrExtrapMs));
        bool hadUnderflow = false;

        foreach (KeyValuePair<int, RemoteEntity> pair in _remotePlayers)
        {
            RemoteEntity remote = pair.Value;
            if (!remote.Buffer.TrySample(client_render_tick, maxExtrapTicks, _config.UseHermiteInterpolation, out RemoteSample sample, out bool underflow))
            {
                continue;
            }
            hadUnderflow |= underflow;

            if (remote.Character.GlobalPosition.DistanceTo(sample.Pos) > InterpGapJumpMeters)
            {
                continue;
            }

            remote.Character.GlobalPosition = sample.Pos;
            remote.Character.Velocity = sample.Vel;
            remote.Character.SetLook(sample.Yaw, sample.Pitch);
        }

        AdjustInterpUnderflowCompensation(hadUnderflow, nowSec);
        _dynamicInterpolationDelayMs = TicksToMs(_globalInterpDelayTicks);
    }

    private void UpdateSessionSnapshotJitter(double arrivalNowSec)
    {
        if (_hasSnapshotArrivalTimeSec)
        {
            double arrivalDeltaSec = arrivalNowSec - _lastSnapshotArrivalTimeSec;
            double expectedDeltaSec = 1.0 / Mathf.Max(1, _config.SnapshotRate);
            float jitterSampleMs = Mathf.Abs((float)((arrivalDeltaSec - expectedDeltaSec) * 1000.0));
            _sessionSnapshotJitterEwmaMs = Mathf.Lerp(
                _sessionSnapshotJitterEwmaMs,
                jitterSampleMs,
                SessionJitterEwmaAlpha);
        }

        _lastSnapshotArrivalTimeSec = arrivalNowSec;
        _hasSnapshotArrivalTimeSec = true;
    }

    private static long GetLocalUsec()
    {
        return (long)Time.GetTicksUsec();
    }

    private uint GetEstimatedServerTickNow()
    {
        long nowUsec = GetLocalUsec();
        if (_netClock is null || _netClock.LastServerTick == 0)
        {
            return _lastAuthoritativeServerTick > 0 ? _lastAuthoritativeServerTick : _server_sim_tick;
        }

        return _netClock.GetEstimatedServerTick(nowUsec);
    }

    private void ObserveAuthoritativeServerTick(uint serverTick, long localUsec)
    {
        _lastAuthoritativeServerTick = serverTick;
        _netClock?.ObserveServerTick(serverTick, localUsec);
    }

    private int MsToTicks(float milliseconds)
    {
        float tickMs = 1000.0f / Mathf.Max(1, _config.ServerTickRate);
        return Mathf.CeilToInt(Mathf.Max(0.0f, milliseconds) / tickMs);
    }

    private float TicksToMs(int ticks)
    {
        if (ticks <= 0)
        {
            return 0.0f;
        }

        float tickMs = 1000.0f / Mathf.Max(1, _config.ServerTickRate);
        return ticks * tickMs;
    }

    private void UpdateGlobalInterpDelayTicks(int targetTicks, double nowSec)
    {
        if (_globalInterpDelayTicks <= 0)
        {
            _globalInterpDelayTicks = targetTicks;
            _nextInterpDelayStepAtSec = nowSec + NetConstants.InputDelayUpdateIntervalSec;
            return;
        }

        if (nowSec < _nextInterpDelayStepAtSec)
        {
            return;
        }

        _nextInterpDelayStepAtSec = nowSec + NetConstants.InputDelayUpdateIntervalSec;
        int delta = targetTicks - _globalInterpDelayTicks;
        if (delta > 0)
        {
            _globalInterpDelayTicks++;
        }
        else if (delta < 0)
        {
            _globalInterpDelayTicks--;
        }
    }

    private void AdjustInterpUnderflowCompensation(bool hadUnderflow, double nowSec)
    {
        if (hadUnderflow)
        {
            if (_nextInterpUnderflowAdjustAtSec <= 0.0 || nowSec >= _nextInterpUnderflowAdjustAtSec)
            {
                _interpUnderflowExtraTicks++;
                _nextInterpUnderflowAdjustAtSec = nowSec + 1.0;
            }

            return;
        }

        if (_interpUnderflowExtraTicks > 0 && (_nextInterpUnderflowAdjustAtSec <= 0.0 || nowSec >= _nextInterpUnderflowAdjustAtSec))
        {
            _interpUnderflowExtraTicks--;
            _nextInterpUnderflowAdjustAtSec = nowSec + 2.0;
        }
    }

    private void MaybeResyncClient(string reason)
    {
        if (_mode != RunMode.Client || _netClock is null || _lastAuthoritativeServerTick == 0)
        {
            return;
        }

        uint estimatedTick = _netClock.GetEstimatedServerTick(GetLocalUsec());
        int tickError = Mathf.Abs((int)estimatedTick - (int)_lastAuthoritativeServerTick);
        if (tickError > 4)
        {
            TriggerClientResync(reason, _lastAuthoritativeServerTick);
        }
    }

    private void TriggerClientResync(string reason, uint targetServerTick)
    {
        if (_mode != RunMode.Client || _netClock is null || targetServerTick == 0)
        {
            return;
        }

        uint beforeTick = _netClock.GetEstimatedServerTick(GetLocalUsec());
        int beforeError = (int)beforeTick - (int)targetServerTick;
        long nowUsec = GetLocalUsec();
        _netClock.ForceResync(targetServerTick, nowUsec);
        uint afterTick = _netClock.GetEstimatedServerTick(nowUsec);
        int afterError = (int)afterTick - (int)targetServerTick;

        _pendingInputs.Clear();
        _client_est_server_tick = targetServerTick;

        _lastAckedSeq = _nextInputSeq;
        _localCharacter?.ClearRenderCorrection();
        _localCharacter?.ClearViewCorrection();
        _resyncTriggered = true;
        _resyncCount++;
        GD.Print($"RESYNC: reason={reason} tickError(before/after)={beforeError}/{afterError} targetTick={targetServerTick}");
    }

    private void UpdateAppliedInputDelayTicks()
    {
        double nowSec = Time.GetTicksMsec() / 1000.0;
        if (_delayTicksNextApplyAtSec <= 0.0)
        {
            _delayTicksNextApplyAtSec = nowSec;
        }

        if (nowSec < _delayTicksNextApplyAtSec)
        {
            return;
        }

        bool inJoinGrace = nowSec < _joinDelayGraceUntilSec;
        _delayTicksNextApplyAtSec = nowSec + (inJoinGrace ? 0.5 : NetConstants.InputDelayUpdateIntervalSec);
        int maxAllowed = Mathf.Min(NetConstants.MaxWanInputDelayTicks, Mathf.Max(0, NetConstants.MaxFutureInputTicks - 2));
        _targetInputDelayTicks = Mathf.Clamp(_targetInputDelayTicks, 0, maxAllowed);
        _appliedInputDelayTicks = Mathf.Clamp(_appliedInputDelayTicks, 0, maxAllowed);

        if (inJoinGrace && _targetInputDelayTicks < _joinInitialInputDelayTicks)
        {
            _targetInputDelayTicks = _joinInitialInputDelayTicks;
        }

        int delta = _targetInputDelayTicks - _appliedInputDelayTicks;
        if (delta > 0)
        {
            _appliedInputDelayTicks++;
        }
        else if (delta < 0)
        {
            _appliedInputDelayTicks--;
        }
    }
}
