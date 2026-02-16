// Scripts/Net/NetSession.Control.cs
using Godot;

namespace NetRunnerSlice.Net;

public partial class NetSession
{
    private void HandleControl(int fromPeer, byte[] packet)
    {
        if (!NetCodec.TryReadControl(packet, out ControlType type))
        {
            return;
        }

        if (IsServer)
        {
            switch (type)
            {
                case ControlType.Hello:
                    GD.Print($"NetSession: Hello received from peer {fromPeer}");
                    if (NetCodec.ReadControlProtocol(packet) != NetConstants.ProtocolVersion)
                    {
                        return;
                    }

                    if (!_serverPlayers.ContainsKey(fromPeer))
                    {
                        ServerPeerConnected(fromPeer);
                    }

                    if (!_serverPlayers.TryGetValue(fromPeer, out ServerPlayer? serverPlayer))
                    {
                        return;
                    }

                    UpdateEffectiveInputDelayForPeer(fromPeer, serverPlayer, sendDelayUpdate: false);
                    NetCodec.WriteControlWelcome(
                        _controlPacket,
                        fromPeer,
                        _server_sim_tick,
                        _config.ServerTickRate,
                        _config.ClientTickRate,
                        _config.SnapshotRate,
                        _config.InterpolationDelayMs,
                        _config.MaxExtrapolationMs,
                        _config.ReconciliationSmoothMs,
                        _config.ReconciliationSnapThreshold,
                        _config.PitchClampDegrees,
                        _config.MoveSpeed,
                        _config.GroundAcceleration,
                        _config.AirAcceleration,
                        _config.AirControlFactor,
                        _config.JumpVelocity,
                        _config.Gravity,
                        serverPlayer.EffectiveInputDelayTicks,
                        _config.FloorSnapLength,
                        _config.GroundStickVelocity);
                    SendPacket(fromPeer, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
                    GD.Print($"NetSession: Welcome sent to peer {fromPeer}");
                    break;
                case ControlType.Ping:
                    ushort pingSeq = NetCodec.ReadControlPingSeq(packet);
                    uint clientTime = NetCodec.ReadControlClientTime(packet);
                    NetCodec.WriteControlPong(_controlPacket, pingSeq, clientTime, _server_sim_tick);
                    SendPacket(fromPeer, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
                    break;
                case ControlType.Pong:
                    if (_serverPlayers.TryGetValue(fromPeer, out ServerPlayer? pongPlayer))
                    {
                        HandleServerPong(fromPeer, pongPlayer, packet);
                    }
                    break;
            }
        }

        if (_mode != RunMode.Client)
        {
            return;
        }

        switch (type)
        {
            case ControlType.Welcome:
                GD.Print("NetSession: Welcome received");
                if (NetCodec.ReadControlProtocol(packet) != NetConstants.ProtocolVersion)
                {
                    GD.PushError("Protocol mismatch.");
                    StopSession();
                    return;
                }

                _localPeerId = NetCodec.ReadControlAssignedPeer(packet);
                _config.ServerTickRate = Mathf.Max(1, NetCodec.ReadControlServerTickRate(packet));
                _config.ClientTickRate = Mathf.Max(1, NetCodec.ReadControlClientTickRate(packet));
                _config.SnapshotRate = Mathf.Max(1, NetCodec.ReadControlSnapshotRate(packet));
                _config.InterpolationDelayMs = Mathf.Max(0, NetCodec.ReadControlInterpolationDelayMs(packet));
                _config.MaxExtrapolationMs = Mathf.Max(0, NetCodec.ReadControlMaxExtrapolationMs(packet));
                _config.ReconciliationSmoothMs = Mathf.Max(1, NetCodec.ReadControlReconcileSmoothMs(packet));
                _config.ReconciliationSnapThreshold = Mathf.Max(0.1f, NetCodec.ReadControlReconcileSnapThreshold(packet));
                _config.PitchClampDegrees = Mathf.Clamp(NetCodec.ReadControlPitchClampDegrees(packet), 1.0f, 89.0f);
                _config.MoveSpeed = Mathf.Max(0.1f, NetCodec.ReadControlMoveSpeed(packet));
                _config.GroundAcceleration = Mathf.Max(0.1f, NetCodec.ReadControlGroundAcceleration(packet));
                _config.AirAcceleration = Mathf.Max(0.1f, NetCodec.ReadControlAirAcceleration(packet));
                _config.AirControlFactor = Mathf.Clamp(NetCodec.ReadControlAirControlFactor(packet), 0.0f, 1.0f);
                _config.JumpVelocity = Mathf.Max(0.1f, NetCodec.ReadControlJumpVelocity(packet));
                _config.Gravity = Mathf.Max(0.1f, NetCodec.ReadControlGravity(packet));
                _config.ServerInputDelayTicks = Mathf.Clamp(NetCodec.ReadControlServerInputDelayTicks(packet), 0, NetConstants.MaxWanInputDelayTicks);
                _appliedInputDelayTicks = _config.ServerInputDelayTicks;
                _targetInputDelayTicks = _config.ServerInputDelayTicks;
                _config.FloorSnapLength = Mathf.Clamp(NetCodec.ReadControlFloorSnapLength(packet), 0.0f, 2.0f);
                _config.GroundStickVelocity = Mathf.Min(NetCodec.ReadControlGroundStickVelocity(packet), -0.01f);
                _server_sim_tick = NetCodec.ReadControlWelcomeServerTick(packet);
                _lastAuthoritativeServerTick = _server_sim_tick;
                GD.Print(
                    $"Welcome applied: MoveSpeed={_config.MoveSpeed:0.###}, GroundAccel={_config.GroundAcceleration:0.###}, InputDelayTicks={_config.ServerInputDelayTicks}");
                _netClock = new NetClock(_config.ServerTickRate);
                _netClock.ObserveServerTick(_server_sim_tick, (long)Time.GetTicksUsec());
                RebaseClientTickToServerEstimate();
                double welcomeNowSec = Time.GetTicksMsec() / 1000.0;
                _joinInitialInputDelayTicks = _appliedInputDelayTicks;
                _joinDelayGraceUntilSec = welcomeNowSec + 2.0;
                _delayTicksNextApplyAtSec = welcomeNowSec;
                _clientJoinDiagUntilSec = welcomeNowSec + 3.0;
                _clientNextJoinDiagAtSec = welcomeNowSec;
                _clientInputCmdsSentSinceLastDiag = 0;
                uint desired_horizon_tick = GetDesiredHorizonTick();
                int warmupSent = SendInputsUpToDesiredHorizon(desired_horizon_tick, allowPrediction: false);
                _clientInputCmdsSentSinceLastDiag += warmupSent;
                GD.Print(
                    $"NetSession: Warmup input burst complete. sent={warmupSent} client_send_tick={_client_send_tick} desired_horizon_tick={desired_horizon_tick}");
                _welcomeReceived = true;
                TrySpawnLocalCharacter();
                break;
            case ControlType.Ping:
                ushort pingSeq = NetCodec.ReadControlPingSeq(packet);
                uint senderTime = NetCodec.ReadControlClientTime(packet);
                NetCodec.WriteControlPong(_controlPacket, pingSeq, senderTime, _server_sim_tick);
                SendPacket(1, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
                break;
            case ControlType.Pong:
                ushort pongSeq = NetCodec.ReadControlPingSeq(packet);
                if (_pingSent.TryGetValue(pongSeq, out double sendSec))
                {
                    double nowSec = Time.GetTicksMsec() / 1000.0;
                    float sampleRtt = (float)((nowSec - sendSec) * 1000.0);
                    _pingSent.Remove(pongSeq);

                    if (_rttMs <= 0.01f)
                    {
                        _rttMs = sampleRtt;
                    }
                    else
                    {
                        float deltaRtt = Mathf.Abs(sampleRtt - _rttMs);
                        _rttMs = Mathf.Lerp(_rttMs, sampleRtt, NetConstants.RttEwmaAlpha);
                        _jitterMs = Mathf.Lerp(_jitterMs, deltaRtt, NetConstants.RttEwmaAlpha);
                    }

                    uint serverTick = NetCodec.ReadControlServerTick(packet);
                    _server_sim_tick = serverTick;
                    _lastAuthoritativeServerTick = serverTick;
                    _netClock?.ObserveServerTick(serverTick, (long)Time.GetTicksUsec());
                }
                break;
            case ControlType.DelayUpdate:
                int delayTicks = Mathf.Clamp(
                    NetCodec.ReadControlDelayTicks(packet),
                    0,
                    Mathf.Min(NetConstants.MaxWanInputDelayTicks, Mathf.Max(0, NetConstants.MaxFutureInputTicks - 2)));
                if (_targetInputDelayTicks != delayTicks)
                {
                    _targetInputDelayTicks = delayTicks;
                    _config.ServerInputDelayTicks = delayTicks;
                    GD.Print($"NetSession: DelayUpdate received => InputDelayTicks={delayTicks}");
                }
                break;
            case ControlType.ResyncHint:
                uint hintServerTick = NetCodec.ReadControlResyncHintTick(packet);
                _server_sim_tick = hintServerTick;
                _lastAuthoritativeServerTick = hintServerTick;
                _netClock?.ObserveServerTick(hintServerTick, (long)Time.GetTicksUsec());
                TriggerClientResync("server_resync_hint", hintServerTick);
                break;
        }
    }

    private void HandleServerPong(int fromPeer, ServerPlayer serverPlayer, byte[] packet)
    {
        ushort pongSeq = NetCodec.ReadControlPingSeq(packet);
        if (!serverPlayer.PendingPings.TryGetValue(pongSeq, out double sendSec))
        {
            return;
        }

        serverPlayer.PendingPings.Remove(pongSeq);
        double nowSec = Time.GetTicksMsec() / 1000.0;
        float sampleRtt = (float)((nowSec - sendSec) * 1000.0);
        if (serverPlayer.RttMs <= 0.01f)
        {
            serverPlayer.RttMs = sampleRtt;
        }
        else
        {
            float deltaRtt = Mathf.Abs(sampleRtt - serverPlayer.RttMs);
            serverPlayer.RttMs = Mathf.Lerp(serverPlayer.RttMs, sampleRtt, NetConstants.RttEwmaAlpha);
            serverPlayer.JitterMs = Mathf.Lerp(serverPlayer.JitterMs, deltaRtt, NetConstants.RttEwmaAlpha);
        }

        UpdateEffectiveInputDelayForPeer(fromPeer, serverPlayer, sendDelayUpdate: true, nowSec);
    }
}
