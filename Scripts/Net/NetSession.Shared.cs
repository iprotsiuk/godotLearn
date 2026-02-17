// Scripts/Net/NetSession.Shared.cs
using Godot;
using NetRunnerSlice.Player;
using NetRunnerSlice.Player.Locomotion;

namespace NetRunnerSlice.Net;

public partial class NetSession
{
    private bool IsTransportConnected()
    {
        MultiplayerPeer? peer = Multiplayer.MultiplayerPeer;
        if (peer is null || _sceneMultiplayer is null)
        {
            return false;
        }

        return peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;
    }

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
            GD.Print($"PktRecv: type={(byte)packetType} ({packetType}) from={fromPeer} len={packet.Length} serverTick={_server_sim_tick}");
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
        if (channel == NetChannels.Input && mode == MultiplayerPeer.TransferModeEnum.Reliable)
        {
            GD.PushError("Input packets must not use Reliable mode. Use Unreliable on NetChannels.Input.");
            return;
        }

        if (_simulator is null)
        {
            SendPacketNow(targetPeer, channel, mode, packet);
            return;
        }

        _simulator.EnqueueSend(Time.GetTicksMsec() / 1000.0, targetPeer, channel, mode, packet);
    }

    private void SendPacketNow(int targetPeer, int channel, MultiplayerPeer.TransferModeEnum mode, byte[] packet)
    {
        SceneMultiplayer? sceneMultiplayer = _sceneMultiplayer;
        if (!IsTransportConnected() || sceneMultiplayer is null)
        {
            return;
        }

        Error err = sceneMultiplayer.SendBytes(packet, targetPeer, mode, channel);
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

        character.Setup(peerId, localCamera, TintForPeer(peerId, localCamera), _config.LocalFov);
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
            : Mathf.Clamp(
                _config.ServerInputDelayTicks,
                NetConstants.MinWanInputDelayTicks,
                Mathf.Min(NetConstants.MaxWanInputDelayTicks, Mathf.Max(0, NetConstants.MaxFutureInputTicks - 2)));

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
            NextPingAtSec = (Time.GetTicksMsec() / 1000.0) + NetConstants.PingIntervalSec,
            JoinDelayGraceUntilSec = (Time.GetTicksMsec() / 1000.0) + 2.0,
            JoinDiagUntilSec = (Time.GetTicksMsec() / 1000.0) + 3.0,
            NextJoinDiagAtSec = Time.GetTicksMsec() / 1000.0
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
        if (IsClient && _lastAuthoritativeServerTick > 0)
        {
            uint estimatedNow = GetEstimatedServerTickNow();
            _tickErrorTicks = (int)estimatedNow - (int)_lastAuthoritativeServerTick;
        }

        if (IsClient)
        {
            double nowSec = Time.GetTicksMsec() / 1000.0;
            _snapshotAgeMs = _lastAuthoritativeSnapshotAtSec > 0.0
                ? (float)((nowSec - _lastAuthoritativeSnapshotAtSec) * 1000.0)
                : -1.0f;
        }
        else
        {
            _snapshotAgeMs = -1.0f;
        }

        UpdateDropFutureRate(Time.GetTicksMsec() / 1000.0);
        UpdateCorrectionRate(Time.GetTicksMsec() / 1000.0);

        float rttMs = _rttMs;
        float jitterMs = _jitterMs;
        if (_mode == RunMode.ListenServer)
        {
            rttMs = -1.0f;
            jitterMs = -1.0f;
        }
        float dynamicInterpDelayMs = IsClient ? _dynamicInterpolationDelayMs : -1.0f;
        float sessionSnapshotJitterMs = IsClient ? _sessionSnapshotJitterEwmaMs : -1.0f;
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

        int inputDelayTicksMetric = IsClient ? _appliedInputDelayTicks : _config.ServerInputDelayTicks;
        Vector3 renderCorrectionOffset = _localCharacter?.RenderCorrectionOffset ?? Vector3.Zero;
        Vector3 viewCorrectionOffset = _localCharacter?.ViewCorrectionOffset ?? Vector3.Zero;
        Vector3 cameraCorrectionOffset = _localCharacter?.CameraCorrectionOffset ?? Vector3.Zero;
        LocomotionState localLocomotionState = _localCharacter?.GetLocomotionState() ?? default;
        Metrics = new SessionMetrics
        {
            FramesPerSecond = (float)Engine.GetFramesPerSecond(),
            ServerSimTick = _server_sim_tick,
            ClientEstServerTick = _client_est_server_tick,
            LastAckedInput = _lastAckedSeq,
            PendingInputCount = _pendingInputs.Count,
            JumpRepeatRemaining = _jumpPressRepeatTicksRemaining,
            LastCorrectionMagnitude = _lastCorrectionMeters,
            CorrXZ = _lastCorrectionXZMeters,
            CorrY = _lastCorrectionYMeters,
            Corr3D = _lastCorrection3DMeters,
            CorrectionsPerSec = _correctionsPerSec,
            RenderCorrectionOffset = renderCorrectionOffset,
            ViewCorrectionOffset = viewCorrectionOffset,
            CameraCorrectionOffset = cameraCorrectionOffset,
            RttMs = rttMs,
            JitterMs = jitterMs,
            LocalGrounded = _localCharacter?.Grounded ?? false,
            LocalLocomotionMode = (byte)localLocomotionState.Mode,
            LocalWallRunTicksRemaining = localLocomotionState.WallRunTicksRemaining,
            LocalSlideTicksRemaining = localLocomotionState.SlideTicksRemaining,
            MoveSpeed = _config.MoveSpeed,
            GroundAcceleration = _config.GroundAcceleration,
            ServerInputDelayTicks = inputDelayTicksMetric,
            NetworkSimulationEnabled = _simEnabled,
            SimLatencyMs = _simLatency,
            SimJitterMs = _simJitter,
            SimLossPercent = _simLoss,
            DynamicInterpolationDelayMs = dynamicInterpDelayMs,
            SessionJitterEstimateMs = sessionSnapshotJitterMs,
            SnapshotAgeMs = _snapshotAgeMs,
            TickErrorTicks = _tickErrorTicks,
            ClientSendTick = _client_send_tick > 0 ? _client_send_tick - 1 : 0,
            DropFutureRatePerSec = _dropFutureRatePerSec,
            PendingInputsCap = NetConstants.PendingInputHardCap,
            ResyncTriggered = _resyncTriggered,
            ResyncCount = _resyncCount,
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

    private void UpdateDropFutureRate(double nowSec)
    {
        if (_dropFutureRateWindowStartSec <= 0.0)
        {
            _dropFutureRateWindowStartSec = nowSec;
            _dropFutureRateWindowCount = _serverDroppedFutureInputCount;
            _dropFutureRatePerSec = 0.0f;
            return;
        }

        double windowSec = nowSec - _dropFutureRateWindowStartSec;
        if (windowSec < 5.0)
        {
            return;
        }

        uint delta = _serverDroppedFutureInputCount >= _dropFutureRateWindowCount
            ? _serverDroppedFutureInputCount - _dropFutureRateWindowCount
            : _serverDroppedFutureInputCount;
        _dropFutureRatePerSec = windowSec > 0.001 ? (float)(delta / windowSec) : 0.0f;
        _dropFutureRateWindowStartSec = nowSec;
        _dropFutureRateWindowCount = _serverDroppedFutureInputCount;
    }

    private void UpdateCorrectionRate(double nowSec)
    {
        if (!IsClient)
        {
            _correctionsPerSec = 0.0f;
            _correctionRateWindowStartSec = nowSec;
            _correctionRateWindowCount = 0;
            return;
        }

        if (_correctionRateWindowStartSec <= 0.0)
        {
            _correctionRateWindowStartSec = nowSec;
            _correctionRateWindowCount = 0;
            _correctionsPerSec = 0.0f;
            return;
        }

        double elapsed = nowSec - _correctionRateWindowStartSec;
        if (elapsed < 1.0)
        {
            return;
        }

        _correctionsPerSec = elapsed > 0.001 ? (float)(_correctionRateWindowCount / elapsed) : 0.0f;
        _correctionRateWindowStartSec = nowSec;
        _correctionRateWindowCount = 0;
    }
}
