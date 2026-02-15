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
                        _serverTick,
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
                    NetCodec.WriteControlPong(_controlPacket, pingSeq, clientTime, _serverTick);
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
                _config.FloorSnapLength = Mathf.Clamp(NetCodec.ReadControlFloorSnapLength(packet), 0.0f, 2.0f);
                _config.GroundStickVelocity = Mathf.Min(NetCodec.ReadControlGroundStickVelocity(packet), -0.01f);
                _serverTick = NetCodec.ReadControlWelcomeServerTick(packet);
                GD.Print(
                    $"Welcome applied: MoveSpeed={_config.MoveSpeed:0.###}, GroundAccel={_config.GroundAcceleration:0.###}, InputDelayTicks={_config.ServerInputDelayTicks}");
                _netClock = new NetClock(_config.ServerTickRate);
                double welcomeNowSec = Time.GetTicksMsec() / 1000.0;
                _netClock.ObserveServerTick(_serverTick, welcomeNowSec, _rttMs);
                RebaseClientTickToServerEstimate();
                _welcomeReceived = true;
                TrySpawnLocalCharacter();
                break;
            case ControlType.Ping:
                ushort pingSeq = NetCodec.ReadControlPingSeq(packet);
                uint senderTime = NetCodec.ReadControlClientTime(packet);
                NetCodec.WriteControlPong(_controlPacket, pingSeq, senderTime, _serverTick);
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
                    _netClock?.ObserveServerTick(serverTick, nowSec, _rttMs);
                }
                break;
            case ControlType.DelayUpdate:
                int delayTicks = Mathf.Clamp(NetCodec.ReadControlDelayTicks(packet), 0, NetConstants.MaxWanInputDelayTicks);
                if (_config.ServerInputDelayTicks != delayTicks)
                {
                    _config.ServerInputDelayTicks = delayTicks;
                    GD.Print($"NetSession: DelayUpdate received => InputDelayTicks={delayTicks}");
                }
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
