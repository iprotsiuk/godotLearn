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
    private const float ReconcileDeadzoneXZMeters = 0.015f;
    private const float ReconcileDeadzoneYMeters = 0.010f;
    private const uint FutureDropBurstThreshold = 10;
    private const double FutureDropBurstWindowSec = 1.0;
    private double _nextSnapshotRecvDiagAtSec;
    private double _reconcileAppliedWindowStartSec;
    private int _reconcileAppliedCountWindow;
    private float _lastAppliedRenderOffsetLen;
    private float _lastAppliedViewOffsetAbs;
    private ulong _lastClientTickUsec;
    private double _nextClientRealtimeStallResyncAtSec;
    private uint _lastSeenDroppedFutureInputCount;
    private double _lastSeenDroppedFutureAtSec;
    private double _nextServerFutureDropGuardResyncAtSec;

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
        ulong nowUsec = (ulong)GetLocalUsec();
        float realDeltaSec = 0.0f;
        if (_lastClientTickUsec != 0)
        {
            realDeltaSec = nowUsec >= _lastClientTickUsec
                ? (nowUsec - _lastClientTickUsec) / 1_000_000.0f
                : NetConstants.StallEpochThresholdSeconds + 1.0f;
        }

        if (realDeltaSec > NetConstants.StallEpochThresholdSeconds)
        {
            AdvanceInputEpoch(resetTickToServerEstimate: true);
            double stallNowSec = nowUsec / 1_000_000.0;
            if (stallNowSec >= _nextClientRealtimeStallResyncAtSec)
            {
                TriggerClientResync("client_realtime_stall", _lastAuthoritativeServerTick);
                _nextClientRealtimeStallResyncAtSec = stallNowSec + 0.5;
            }
        }

        if (_mode == RunMode.Client && !IsTransportConnected())
        {
            _lastClientTickUsec = nowUsec;
            return;
        }

        _client_est_server_tick = GetEstimatedServerTickNow();
        MaybeResyncClient("tick_drift_guard");
        UpdateAppliedInputDelayTicks();

        if (_lastAuthoritativeServerTick > 0)
        {
            uint maxSafeSendTick = _lastAuthoritativeServerTick + (uint)NetConstants.MaxFutureInputTicks - 1;
            if (_client_send_tick > maxSafeSendTick)
            {
                TriggerClientResync("future_tick_guard", _lastAuthoritativeServerTick);
                _client_est_server_tick = GetEstimatedServerTickNow();
            }
        }

        uint desired_horizon_tick = GetDesiredHorizonTick();
        if (_lastAuthoritativeServerTick > 0)
        {
            uint maxSafeSendTick = _lastAuthoritativeServerTick + (uint)NetConstants.MaxFutureInputTicks - 1;
            if (desired_horizon_tick > maxSafeSendTick)
            {
                desired_horizon_tick = maxSafeSendTick;
            }
        }

        if (_localCharacter is not null && desired_horizon_tick < _client_send_tick)
        {
            desired_horizon_tick = _client_send_tick;
        }

        int sentThisTick = SendInputsUpToDesiredHorizon(desired_horizon_tick, allowPrediction: _localCharacter is not null);
        if (_logControlPackets && _localCharacter is not null && sentThisTick == 0)
        {
            GD.Print(
                $"PredictDiagZeroStep: client_est_server_tick={_client_est_server_tick} " +
                $"client_send_tick={_client_send_tick} desired_horizon_tick={desired_horizon_tick}");
        }

        _clientInputCmdsSentSinceLastDiag += sentThisTick;
        LogClientJoinDiagnosticsIfDue(desired_horizon_tick);

        if (_mode != RunMode.Client)
        {
            _lastClientTickUsec = nowUsec;
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

        _lastClientTickUsec = nowUsec;
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

        _client_send_tick = _client_est_server_tick + 1;
    }

    private int SendInputsUpToDesiredHorizon(uint desired_horizon_tick, bool allowPrediction)
    {
        if (!IsClient)
        {
            return 0;
        }

        uint minSendTick = _client_est_server_tick + 1;
        if (_client_send_tick < minSendTick)
        {
            _client_send_tick = minSendTick;
        }

        int generatedCount = 0;
        int packetCount = 0;
        uint firstGeneratedSeq = 0;
        uint lastGeneratedSeq = 0;
        while (_client_send_tick <= desired_horizon_tick)
        {
            InputCommand command = BuildInputCommandForTick(_client_send_tick);
            _pendingInputs.Add(command);
            if (generatedCount == 0)
            {
                firstGeneratedSeq = command.Seq;
            }
            lastGeneratedSeq = command.Seq;
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

            int sendCount = _pendingInputs.GetLatest(NetConstants.MaxInputRedundancy, _inputSendScratch.AsSpan(), _nextInputSeq);
            if (sendCount <= 0)
            {
                break;
            }

            NetCodec.WriteInputBundle(_inputPacket, _inputSendScratch.AsSpan(0, sendCount));
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

        if (generatedCount > 1)
        {
            GD.Print($"InputSendDiag: generated={generatedCount} packets={packetCount} seq_range={firstGeneratedSeq}..{lastGeneratedSeq}");
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
        _clientNextJoinDiagAtSec = nowSec + JoinDiagnosticsLogIntervalSec;
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
        float recvGapMs = _hasSnapshotArrivalTimeSec
            ? (float)((localNowSec - _lastSnapshotArrivalTimeSec) * 1000.0)
            : -1.0f;
        float lastSnapshotAgeMs = _lastAuthoritativeSnapshotAtSec > 0.0
            ? (float)((localNowSec - _lastAuthoritativeSnapshotAtSec) * 1000.0)
            : -1.0f;
        _lastAuthoritativeSnapshotAtSec = localNowSec;
        ObserveAuthoritativeServerTick(serverTick, localNowUsec);
        _server_sim_tick = serverTick;
        _lastAuthoritativeServerTick = serverTick;

        UpdateSessionSnapshotJitter(localNowSec);
        if (localNowSec >= _nextSnapshotRecvDiagAtSec)
        {
            _nextSnapshotRecvDiagAtSec = localNowSec + 1.0;
            GD.Print(
                $"SnapshotRecvDiag: tick={serverTick} count={count} bytes={packet.Length} recv_gap_ms={recvGapMs:0.0} last_age_ms={lastSnapshotAgeMs:0.0}");
        }

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
        double snapshotNowSec = Time.GetTicksMsec() / 1000.0;
        if (_lastSeenDroppedFutureAtSec > 0.0)
        {
            double dtSec = snapshotNowSec - _lastSeenDroppedFutureAtSec;
            uint droppedFutureDelta = snapshot.DroppedFutureInputCount >= _lastSeenDroppedFutureInputCount
                ? snapshot.DroppedFutureInputCount - _lastSeenDroppedFutureInputCount
                : snapshot.DroppedFutureInputCount;
            if (dtSec <= FutureDropBurstWindowSec &&
                droppedFutureDelta > FutureDropBurstThreshold &&
                snapshotNowSec >= _nextServerFutureDropGuardResyncAtSec)
            {
                TriggerClientResync("server_future_drop_guard", _lastAuthoritativeServerTick);
                _nextServerFutureDropGuardResyncAtSec = snapshotNowSec + 0.5;
            }
        }

        _lastSeenDroppedFutureInputCount = snapshot.DroppedFutureInputCount;
        _lastSeenDroppedFutureAtSec = snapshotNowSec;

        Vector3 before = _localCharacter.GlobalPosition;
        _localCharacter.GlobalPosition = snapshot.Pos;
        _localCharacter.Velocity = snapshot.Vel;
        LocomotionNetState authoritativeNetState = new(
            snapshot.LocoMode,
            snapshot.LocoWallNormalX,
            snapshot.LocoWallNormalZ,
            snapshot.LocoWallRunTicksRemaining,
            snapshot.LocoSlideTicksRemaining);
        _localCharacter.SetLocomotionState(
            LocomotionNetStateCodec.UnpackToLocomotionState(snapshot.Grounded, authoritativeNetState));
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
        Vector3 rawDelta = before - after;
        float rawCorrXZ = new Vector2(rawDelta.X, rawDelta.Z).Length();
        float rawCorrY = Mathf.Abs(rawDelta.Y);
        Vector3 metricDelta = rawDelta;
        bool ignoreSmallXZ = rawCorrXZ < ReconcileDeadzoneXZMeters;
        bool ignoreSmallY = rawCorrY < ReconcileDeadzoneYMeters;
        if (ignoreSmallXZ)
        {
            metricDelta.X = 0.0f;
            metricDelta.Z = 0.0f;
        }

        if (ignoreSmallY)
        {
            metricDelta.Y = 0.0f;
        }

        float corrXZ = new Vector2(metricDelta.X, metricDelta.Z).Length();
        float corrY = Mathf.Abs(metricDelta.Y);
        float corr3D = metricDelta.Length();
        uint totalUsage = snapshot.TicksUsedBufferedInput + snapshot.TicksUsedHoldLast + snapshot.TicksUsedNeutral;
        float bufferedPct = totalUsage == 0
            ? 0.0f
            : (100.0f * snapshot.TicksUsedBufferedInput) / totalUsage;
        _lastCorrectionXZMeters = corrXZ;
        _lastCorrectionYMeters = corrY;
        _lastCorrection3DMeters = corr3D;
        _lastCorrectionMeters = corr3D;
        if (corr3D > 0.0005f)
        {
            _correctionRateWindowCount++;
        }

        // Deadzone must affect visual correction offsets, not only metrics, otherwise tiny noise still jitters camera/mesh.
        Vector3 renderOffset = new(metricDelta.X, 0.0f, metricDelta.Z);
        int correctionsAppliedThisSample = 0;
        bool hardSnapXZ = rawCorrXZ > _config.ReconciliationSnapThreshold;
        if (hardSnapXZ)
        {
            GD.Print(
                $"ReconcileSnap: serverTick={_lastAuthoritativeServerTick} lastAckedInputSeq={_lastAckedSeq} " +
                $"predictedBeforePos={before.X:0.###},{before.Y:0.###},{before.Z:0.###} " +
                $"predictedAfterPos={after.X:0.###},{after.Y:0.###},{after.Z:0.###} " +
                $"rawCorrXZ={rawCorrXZ:0.####} rawCorrY={rawCorrY:0.####} hardSnapXZ=true " +
                $"pendingInputsCount={_pendingInputs.Count} clientSendTick={_client_send_tick} clientEstServerTick={_client_est_server_tick} " +
                $"droppedFutureInputCount={snapshot.DroppedFutureInputCount} missingInputStreakCurrent={snapshot.MissingInputStreakCurrent} bufferedPct={bufferedPct:0.0}%");
            _localCharacter.ClearRenderCorrection();
            _localCharacter.ClearViewCorrection();
            _localCharacter.ResetInterpolationAfterSnap();
            _lastAppliedRenderOffsetLen = 0.0f;
        }
        else if (renderOffset.LengthSquared() > 0.000001f)
        {
            GD.Print(
                $"ReconcileCorrection: serverTick={_lastAuthoritativeServerTick} lastAckedInputSeq={_lastAckedSeq} " +
                $"predictedBeforePos={before.X:0.###},{before.Y:0.###},{before.Z:0.###} " +
                $"predictedAfterPos={after.X:0.###},{after.Y:0.###},{after.Z:0.###} " +
                $"rawCorrXZ={rawCorrXZ:0.####} rawCorrY={rawCorrY:0.####} hardSnapXZ=false " +
                $"pendingInputsCount={_pendingInputs.Count} clientSendTick={_client_send_tick} clientEstServerTick={_client_est_server_tick} " +
                $"droppedFutureInputCount={snapshot.DroppedFutureInputCount} missingInputStreakCurrent={snapshot.MissingInputStreakCurrent} bufferedPct={bufferedPct:0.0}%");
            _localCharacter.AddRenderCorrection(renderOffset, _config.ReconciliationSmoothMs);
            _lastAppliedRenderOffsetLen = renderOffset.Length();
            correctionsAppliedThisSample++;
        }
        else
        {
            _lastAppliedRenderOffsetLen = 0.0f;
        }

        Vector3 viewOffset = new(0.0f, metricDelta.Y, 0.0f);
        if (hardSnapXZ || rawCorrY > 0.5f)
        {
            _localCharacter.ClearViewCorrection();
            _lastAppliedViewOffsetAbs = 0.0f;
        }
        else if (Mathf.Abs(viewOffset.Y) > 0.000001f)
        {
            int viewSmoothMs = _config.ReconciliationSmoothMs;
            _localCharacter.AddViewCorrection(viewOffset, viewSmoothMs);
            _lastAppliedViewOffsetAbs = Mathf.Abs(viewOffset.Y);
            correctionsAppliedThisSample++;
            if (_logControlPackets && rawCorrY > 0.0005f)
            {
                GD.Print(
                    $"ReconcileViewDiag: serverTick={_lastAuthoritativeServerTick} ack={_lastAckedSeq} " +
                    $"rawDeltaY={rawDelta.Y:0.####} rawCorrY={rawCorrY:0.####} smoothMs={viewSmoothMs}");
            }
        }
        else
        {
            _lastAppliedViewOffsetAbs = 0.0f;
        }

        double nowSec = snapshotNowSec;
        if (_reconcileAppliedWindowStartSec <= 0.0)
        {
            _reconcileAppliedWindowStartSec = nowSec;
        }

        _reconcileAppliedCountWindow += correctionsAppliedThisSample;
        if ((nowSec - _reconcileAppliedWindowStartSec) >= 1.0)
        {
            double windowSec = nowSec - _reconcileAppliedWindowStartSec;
            float appliedPerSec = windowSec > 0.000001
                ? (float)(_reconcileAppliedCountWindow / windowSec)
                : 0.0f;
            GD.Print(
                $"ReconcileApplyDiag: corrections_applied_per_sec={appliedPerSec:0.00} " +
                $"last_applied_render_offset_len={_lastAppliedRenderOffsetLen:0.####} " +
                $"last_applied_view_offset_abs={_lastAppliedViewOffsetAbs:0.####}");
            _reconcileAppliedWindowStartSec = nowSec;
            _reconcileAppliedCountWindow = 0;
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
                remote.Character.GlobalPosition = sample.Pos;
                remote.Character.Velocity = sample.Vel;
                remote.Character.SetLook(sample.Yaw, sample.Pitch);
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
        if (_mode == RunMode.ListenServer)
        {
            return _server_sim_tick;
        }

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

        long nowUsec = GetLocalUsec();
        uint estimatedTick = _netClock.GetEstimatedServerTick(nowUsec);
        int tickErrorSigned = (int)estimatedTick - (int)_lastAuthoritativeServerTick;
        int tickError = Mathf.Abs(tickErrorSigned);
        if (tickError <= 4)
        {
            _tickDriftGuardBreachCount = 0;
            return;
        }

        double nowSec = nowUsec / 1_000_000.0;
        bool inJoinGrace = _clientWelcomeTimeSec > 0.0 && nowSec < (_clientWelcomeTimeSec + ClientResyncJoinGraceSec);
        if (inJoinGrace && reason == "tick_drift_guard")
        {
            _netClock.NudgeTowardServerTick(_lastAuthoritativeServerTick, nowUsec, 1);
            _resyncSuppressedDuringJoinCount++;
            if (nowSec >= _nextResyncDiagLogAtSec)
            {
                _nextResyncDiagLogAtSec = nowSec + JoinDiagnosticsLogIntervalSec;
                GD.Print(
                    $"RESYNC_SUPPRESSED: reason={reason} tickError={tickErrorSigned} targetTick={_lastAuthoritativeServerTick} " +
                    $"suppressedCount={_resyncSuppressedDuringJoinCount}");
            }
            return;
        }

        if (reason == "tick_drift_guard")
        {
            _netClock.NudgeTowardServerTick(_lastAuthoritativeServerTick, nowUsec, 1);
            _tickDriftGuardBreachCount++;
            double snapshotAgeSec = _lastAuthoritativeSnapshotAtSec > 0.0
                ? nowSec - _lastAuthoritativeSnapshotAtSec
                : double.MaxValue;

            // If snapshots are stale, avoid repeatedly hard-snapping to an old authoritative tick.
            if (snapshotAgeSec > 0.25)
            {
                return;
            }

            if (_tickDriftGuardBreachCount < 3 || tickError < 8 || nowSec < _nextHardResyncAllowedAtSec)
            {
                return;
            }

            _nextHardResyncAllowedAtSec = nowSec + 0.5;
            _tickDriftGuardBreachCount = 0;
        }

        TriggerClientResync(reason, _lastAuthoritativeServerTick);
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
        _client_send_tick = targetServerTick + 1;

        _lastAckedSeq = _nextInputSeq;
        _localCharacter?.ClearRenderCorrection();
        _localCharacter?.ClearViewCorrection();
        _resyncTriggered = true;
        _resyncCount++;
        double nowSec = nowUsec / 1_000_000.0;
        if (nowSec >= _nextResyncDiagLogAtSec)
        {
            _nextResyncDiagLogAtSec = nowSec + JoinDiagnosticsLogIntervalSec;
            GD.Print(
                $"RESYNC: reason={reason} tickError(before/after)={beforeError}/{afterError} targetTick={targetServerTick} " +
                $"count={_resyncCount} suppressedDuringJoin={_resyncSuppressedDuringJoinCount}");
        }
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
