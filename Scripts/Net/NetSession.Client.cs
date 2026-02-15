// Scripts/Net/NetSession.Client.cs
using System.Collections.Generic;
using Godot;
using NetRunnerSlice.Player;

namespace NetRunnerSlice.Net;

public partial class NetSession
{
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

        _clientTick++;
        if (_localCharacter is null)
        {
            return;
        }

        InputCommand input = BuildInputCommand();

        _pendingInputs.Add(input);
        _localCharacter.SetLook(input.Yaw, input.Pitch);
        PlayerMotor.Simulate(_localCharacter, input, _config);

        int count = _pendingInputs.GetLatest(NetConstants.MaxInputRedundancy, _inputSendScratch, input.Seq);
        if (count > 0)
        {
            NetCodec.WriteInputBundle(_inputPacket, _inputSendScratch.AsSpan(0, count));
            if (_mode == RunMode.ListenServer)
            {
                HandleInputBundle(_localPeerId, _inputPacket);
            }
            else
            {
                SendPacket(1, NetChannels.Input, MultiplayerPeer.TransferModeEnum.Unreliable, _inputPacket);
            }
        }

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

        return Mathf.Max(0, _config.ServerInputDelayTicks);
    }

    private void RebaseClientTickToServerEstimate()
    {
        if (_mode == RunMode.ListenServer)
        {
            _clientTick = _serverTick;
            _lastSentInputTick = 0;
            return;
        }

        uint estimatedTick = _serverTick;
        if (_netClock is not null && _netClock.LastServerTick > 0)
        {
            double nowSec = Time.GetTicksMsec() / 1000.0;
            double estimatedServerTime = _netClock.GetEstimatedServerTime(nowSec);
            estimatedTick = (uint)Mathf.Max(0, Mathf.RoundToInt((float)(estimatedServerTime * _config.ServerTickRate)));
        }

        if (estimatedTick < _serverTick)
        {
            estimatedTick = _serverTick;
        }

        _clientTick = estimatedTick;
        _lastSentInputTick = 0;
    }

    private InputCommand BuildInputCommand()
    {
        float fixedDt = 1.0f / _config.ClientTickRate;

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

        uint inputTick = _clientTick + (uint)GetClientInputDelayTicksForStamping();
        if (inputTick <= _lastSentInputTick)
        {
            inputTick = _lastSentInputTick + 1;
        }
        _lastSentInputTick = inputTick;

        InputCommand command = new()
        {
            Seq = ++_nextInputSeq,
            InputTick = inputTick,
            InputEpoch = _inputEpoch,
            DtFixed = fixedDt,
            MoveAxes = _inputState.MoveAxes,
            Buttons = buttons,
            Yaw = _lookYaw,
            Pitch = _lookPitch
        };
        InputSanitizer.SanitizeClient(ref command, _config);
        return command;
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

        double localNowSec = Time.GetTicksMsec() / 1000.0;
        _netClock?.ObserveServerTick(serverTick, localNowSec, _rttMs);
        _serverTick = serverTick;

        double snapshotServerTime = serverTick / (double)_config.ServerTickRate;
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

            RemoteSample sample = new()
            {
                Pos = state.Pos,
                Vel = state.Vel,
                Yaw = state.Yaw,
                Pitch = state.Pitch,
                Grounded = state.Grounded
            };
            remote.Buffer.Add(snapshotServerTime, sample);
        }
    }

    private void ReconcileLocal(in PlayerStateSnapshot snapshot)
    {
        if (_localCharacter is null)
        {
            return;
        }

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

        double nowSec = Time.GetTicksMsec() / 1000.0;
        double renderTime = _netClock.GetEstimatedServerTime(nowSec) - (_config.InterpolationDelayMs / 1000.0);
        double maxExtrap = _config.MaxExtrapolationMs / 1000.0;

        foreach (KeyValuePair<int, RemoteEntity> pair in _remotePlayers)
        {
            RemoteEntity remote = pair.Value;
            if (!remote.Buffer.TrySample(renderTime, maxExtrap, _config.UseHermiteInterpolation, out RemoteSample sample))
            {
                continue;
            }

            remote.Character.GlobalPosition = sample.Pos;
            remote.Character.Velocity = sample.Vel;
            remote.Character.SetLook(sample.Yaw, sample.Pitch);
        }
    }

}
