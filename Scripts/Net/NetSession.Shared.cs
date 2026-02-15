// Scripts/Net/NetSession.Shared.cs
using Godot;
using NetRunnerSlice.Player;

namespace NetRunnerSlice.Net;

public partial class NetSession
{
    private void OnPeerPacket(long fromPeerLong, byte[] packet)
    {
        if (_mode == RunMode.None || packet.Length == 0)
        {
            return;
        }

        int fromPeer = (int)fromPeerLong;
        PacketType packetType = (PacketType)packet[0];
        if (IsServer && (packetType == PacketType.Fire || (_logControlPackets && packetType == PacketType.Control)))
        {
            GD.Print($"PktRecv: type={(byte)packetType} ({packetType}) from={fromPeer} len={packet.Length} serverTick={_serverTick}");
        }

        switch (packetType)
        {
            case PacketType.InputBundle:
                HandleInputBundle(fromPeer, packet);
                break;
            case PacketType.Snapshot:
                HandleSnapshot(packet);
                break;
            case PacketType.Control:
                HandleControl(fromPeer, packet);
                break;
            case PacketType.Fire:
                HandleFire(fromPeer, packet);
                break;
            case PacketType.FireResult:
                HandleFireResult(packet);
                break;
            case PacketType.FireVisual:
                HandleFireVisual(packet);
                break;
            default:
                if (IsServer)
                {
                    GD.Print($"PktRecvUnknown: typeByte={packet[0]} from={fromPeer} len={packet.Length}");
                }
                break;
        }
    }

    private void SendPacket(int targetPeer, int channel, MultiplayerPeer.TransferModeEnum mode, byte[] packet)
    {
        if (_simulator is null)
        {
            SendPacketNow(targetPeer, channel, mode, packet);
            return;
        }

        _simulator.EnqueueSend(Time.GetTicksMsec() / 1000.0, targetPeer, channel, mode, packet);
    }

    private void SendPacketNow(int targetPeer, int channel, MultiplayerPeer.TransferModeEnum mode, byte[] packet)
    {
        if (Multiplayer.MultiplayerPeer is null || _sceneMultiplayer is null)
        {
            return;
        }

        Error err = _sceneMultiplayer.SendBytes(packet, targetPeer, mode, channel);
        if (err != Error.Ok)
        {
            GD.PushError($"SendBytes failed: {err} (target={targetPeer}, channel={channel}, mode={mode})");
        }
    }

    private void OnPeerConnected(long id)
    {
        if (IsServer)
        {
            ServerPeerConnected((int)id);
        }
    }

    private void OnPeerDisconnected(long id)
    {
        int peerId = (int)id;
        if (_serverPlayers.TryGetValue(peerId, out ServerPlayer? serverPlayer))
        {
            serverPlayer.Character.QueueFree();
            _serverPlayers.Remove(peerId);
        }

        if (_remotePlayers.TryGetValue(peerId, out RemoteEntity? remotePlayer))
        {
            remotePlayer.Character.QueueFree();
            _remotePlayers.Remove(peerId);
        }
    }

    private void OnConnectedToServer()
    {
        ClientConnectedToServer();
    }

    private void OnConnectionFailed()
    {
        GD.PushError("Connection to server failed.");
        StopSession();
    }

    private void OnServerDisconnected()
    {
        GD.PushWarning("Disconnected from server.");
        StopSession();
    }

    private PlayerCharacter CreateCharacter(int peerId, bool localCamera, bool visible = true)
    {
        if (_playerRoot is null)
        {
            throw new System.InvalidOperationException("NetSession.Initialize must be called before Start.");
        }

        PlayerCharacter character = new()
        {
            Name = $"Player_{peerId}"
        };

        character.Setup(peerId, localCamera, TintForPeer(peerId, localCamera));
        character.Visible = visible;
        _playerRoot.AddChild(character);
        character.GlobalPosition = SpawnPointForPeer(peerId);
        character.SetLook(SpawnYawForPeer(), 0.0f);
        return character;
    }

    private void EnsureServerPlayer(int peerId, PlayerCharacter character)
    {
        if (_serverPlayers.ContainsKey(peerId))
        {
            return;
        }

        int initialDelay = (_mode == RunMode.ListenServer && peerId == _localPeerId)
            ? 0
            : Mathf.Clamp(_config.ServerInputDelayTicks, NetConstants.MinWanInputDelayTicks, NetConstants.MaxWanInputDelayTicks);

        InputCommand seedInput = new()
        {
            DtFixed = 1.0f / _config.ServerTickRate,
            InputEpoch = 1,
            Yaw = character.Yaw,
            Pitch = character.Pitch
        };

        _serverPlayers[peerId] = new ServerPlayer
        {
            Character = character,
            LastInput = seedInput,
            LastProcessedSeq = 0,
            EffectiveInputDelayTicks = initialDelay,
            NextPingAtSec = (Time.GetTicksMsec() / 1000.0) + NetConstants.PingIntervalSec
        };
    }

    private Vector3 SpawnPointForPeer(int peerId)
    {
        int slot = Mathf.Abs(peerId) % 8;
        Vector3 offset = new((slot % 4) * 2.5f - 4.0f, 2.0f, (slot / 4) * 2.5f);
        return _hasSpawnOrigin ? _spawnOrigin.Origin + (_spawnOrigin.Basis * offset) : offset;
    }

    private float SpawnYawForPeer()
    {
        return _hasSpawnOrigin ? _spawnYaw : 0.0f;
    }

    private static Color TintForPeer(int peerId, bool local)
    {
        if (local)
        {
            return new Color(0.2f, 0.8f, 0.9f);
        }

        float hue = (peerId % 12) / 12.0f;
        return Color.FromHsv(hue, 0.6f, 0.9f);
    }

    private void UpdateMetrics()
    {
        float rttMs = _rttMs;
        float jitterMs = _jitterMs;
        if (_mode == RunMode.ListenServer)
        {
            rttMs = -1.0f;
            jitterMs = -1.0f;
        }
        float dynamicInterpDelayMs = IsClient ? _dynamicInterpolationDelayMs : -1.0f;
        uint serverDroppedOldInputCount = _serverDroppedOldInputCount;
        uint serverDroppedFutureInputCount = _serverDroppedFutureInputCount;
        uint serverTicksUsedBufferedInput = _serverTicksUsedBufferedInput;
        uint serverTicksUsedHoldLast = _serverTicksUsedHoldLast;
        uint serverTicksUsedNeutral = _serverTicksUsedNeutral;
        uint serverMissingInputStreakCurrent = _serverMissingInputStreakCurrent;
        uint serverMissingInputStreakMax = _serverMissingInputStreakMax;
        int serverEffectiveDelayTicks = _serverEffectiveDelayTicks;
        float serverPeerRttMs = _serverPeerRttMs;
        float serverPeerJitterMs = _serverPeerJitterMs;

        if (IsServer && _localPeerId != 0 && _serverPlayers.TryGetValue(_localPeerId, out ServerPlayer? localServerPlayer))
        {
            serverDroppedOldInputCount = localServerPlayer.DroppedOldInputCount;
            serverDroppedFutureInputCount = localServerPlayer.DroppedFutureInputCount;
            serverTicksUsedBufferedInput = localServerPlayer.TicksUsedBufferedInput;
            serverTicksUsedHoldLast = localServerPlayer.TicksUsedHoldLast;
            serverTicksUsedNeutral = localServerPlayer.TicksUsedNeutral;
            serverMissingInputStreakCurrent = localServerPlayer.MissingInputStreakCurrent;
            serverMissingInputStreakMax = localServerPlayer.MissingInputStreakMax;
            serverEffectiveDelayTicks = localServerPlayer.EffectiveInputDelayTicks;
            serverPeerRttMs = localServerPlayer.RttMs;
            serverPeerJitterMs = localServerPlayer.JitterMs;
            if (_mode == RunMode.ListenServer)
            {
                rttMs = localServerPlayer.RttMs;
                jitterMs = localServerPlayer.JitterMs;
            }
        }

        Metrics = new SessionMetrics
        {
            ServerTick = _serverTick,
            ClientTick = _clientTick,
            LastAckedInput = _lastAckedSeq,
            PendingInputCount = _pendingInputs.Count,
            JumpRepeatRemaining = _jumpPressRepeatTicksRemaining,
            LastCorrectionMagnitude = _lastCorrectionMeters,
            CorrXZ = _lastCorrectionXZMeters,
            CorrY = _lastCorrectionYMeters,
            Corr3D = _lastCorrection3DMeters,
            RttMs = rttMs,
            JitterMs = jitterMs,
            LocalGrounded = _localCharacter?.Grounded ?? false,
            MoveSpeed = _config.MoveSpeed,
            GroundAcceleration = _config.GroundAcceleration,
            ServerInputDelayTicks = _config.ServerInputDelayTicks,
            NetworkSimulationEnabled = _simEnabled,
            SimLatencyMs = _simLatency,
            SimJitterMs = _simJitter,
            SimLossPercent = _simLoss,
            DynamicInterpolationDelayMs = dynamicInterpDelayMs,
            ServerDroppedOldInputCount = serverDroppedOldInputCount,
            ServerDroppedFutureInputCount = serverDroppedFutureInputCount,
            ServerTicksUsedBufferedInput = serverTicksUsedBufferedInput,
            ServerTicksUsedHoldLast = serverTicksUsedHoldLast,
            ServerTicksUsedNeutral = serverTicksUsedNeutral,
            ServerMissingInputStreakCurrent = serverMissingInputStreakCurrent,
            ServerMissingInputStreakMax = serverMissingInputStreakMax,
            ServerEffectiveDelayTicks = serverEffectiveDelayTicks,
            ServerPeerRttMs = serverPeerRttMs,
            ServerPeerJitterMs = serverPeerJitterMs
        };
    }
}
