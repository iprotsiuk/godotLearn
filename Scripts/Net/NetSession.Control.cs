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

                    NetCodec.WriteControlWelcome(
                        _controlPacket,
                        fromPeer,
                        _config.ServerTickRate,
                        _config.ClientTickRate,
                        _config.SnapshotRate,
                        _config.InterpolationDelayMs,
                        _config.MaxExtrapolationMs,
                        _config.ReconciliationSmoothMs,
                        _config.ReconciliationSnapThreshold,
                        _config.PitchClampDegrees);
                    SendPacket(fromPeer, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
                    GD.Print($"NetSession: Welcome sent to peer {fromPeer}");
                    break;
                case ControlType.Ping:
                    ushort pingSeq = NetCodec.ReadControlPingSeq(packet);
                    uint clientTime = NetCodec.ReadControlClientTime(packet);
                    NetCodec.WriteControlPong(_controlPacket, pingSeq, clientTime, _serverTick);
                    SendPacket(fromPeer, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
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
                _netClock = new NetClock(_config.ServerTickRate);
                _welcomeReceived = true;
                TrySpawnLocalCharacter();
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
                        _rttMs = Mathf.Lerp(_rttMs, sampleRtt, 0.2f);
                        _jitterMs = Mathf.Lerp(_jitterMs, deltaRtt, 0.2f);
                    }

                    uint serverTick = NetCodec.ReadControlServerTick(packet);
                    _netClock?.ObserveServerTick(serverTick, nowSec, _rttMs);
                }
                break;
        }
    }
}
