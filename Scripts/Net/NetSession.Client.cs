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
        _nextPingTimeSec = (Time.GetTicksMsec() / 1000.0) + 0.1;
    }

    private void TickClient(float delta)
    {
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
            NetCodec.WriteControlPing(_controlPacket, _pingSeq, nowMs);
            SendPacket(1, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
            _nextPingTimeSec = nowSec + 0.5;
        }
    }

    private InputCommand BuildInputCommand()
    {
        float fixedDt = 1.0f / _config.ClientTickRate;

        float x = Input.GetActionStrength("move_right") - Input.GetActionStrength("move_left");
        float y = Input.GetActionStrength("move_forward") - Input.GetActionStrength("move_back");

        bool jumpHeld = Input.IsActionPressed("jump");
        bool jumpPressed = jumpHeld && !_jumpHeldLastTick;
        _jumpHeldLastTick = jumpHeld;

        InputButtons buttons = InputButtons.None;
        if (jumpHeld)
        {
            buttons |= InputButtons.JumpHeld;
        }

        if (jumpPressed)
        {
            buttons |= InputButtons.JumpPressed;
        }

        InputCommand command = new()
        {
            Seq = ++_nextInputSeq,
            ClientTick = _clientTick,
            DtFixed = fixedDt,
            MoveAxes = new Vector2(x, y),
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

        _lastAckedSeq = snapshot.LastProcessedSeqForThatClient;
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
        float correction = before.DistanceTo(after);
        _lastCorrectionMeters = correction;

        if (correction > _config.ReconciliationSnapThreshold)
        {
            _localCharacter.ClearRenderCorrection();
        }
        else
        {
            _localCharacter.AddRenderCorrection(before - after, _config.ReconciliationSmoothMs);
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
            if (!remote.Buffer.TrySample(renderTime, maxExtrap, out RemoteSample sample))
            {
                continue;
            }

            remote.Character.GlobalPosition = sample.Pos;
            remote.Character.Velocity = sample.Vel;
            remote.Character.SetLook(sample.Yaw, sample.Pitch);
        }
    }

}
