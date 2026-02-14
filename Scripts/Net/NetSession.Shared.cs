// Scripts/Net/NetSession.Shared.cs
using Godot;
using NetRunnerSlice.Player;

namespace NetRunnerSlice.Net;

public partial class NetSession
{
    private void PollIncomingPackets()
    {
        MultiplayerPeer? peer = Multiplayer.MultiplayerPeer;
        if (peer is null)
        {
            return;
        }

        while (peer.GetAvailablePacketCount() > 0)
        {
            // Godot docs: GetPacketPeer() returns the sender for the next queued packet,
            // so read peer id before GetPacket() to keep sender association correct.
            int fromPeer = peer.GetPacketPeer();
            byte[] packet = peer.GetPacket();

            if (packet.Length == 0)
            {
                continue;
            }

            PacketType packetType = (PacketType)packet[0];
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
            }
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
        MultiplayerPeer? peer = Multiplayer.MultiplayerPeer;
        if (peer is null)
        {
            return;
        }

        peer.TransferChannel = channel;
        peer.TransferMode = mode;
        peer.SetTargetPeer(targetPeer);
        peer.PutPacket(packet);
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
        _localPeerId = Multiplayer.GetUniqueId();
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
        character.GlobalPosition = SpawnPointForPeer(peerId);
        character.SetLook(SpawnYawForPeer(), 0.0f);
        _playerRoot.AddChild(character);
        return character;
    }

    private void EnsureServerPlayer(int peerId, PlayerCharacter character)
    {
        if (_serverPlayers.ContainsKey(peerId))
        {
            return;
        }

        InputCommand seedInput = new()
        {
            DtFixed = 1.0f / _config.ServerTickRate,
            Yaw = character.Yaw,
            Pitch = character.Pitch
        };

        _serverPlayers[peerId] = new ServerPlayer
        {
            Character = character,
            LastInput = seedInput,
            LastProcessedSeq = 0
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
        Metrics = new SessionMetrics
        {
            ServerTick = _serverTick,
            ClientTick = _clientTick,
            LastAckedInput = _lastAckedSeq,
            PendingInputCount = _pendingInputs.Count,
            LastCorrectionMagnitude = _lastCorrectionMeters,
            RttMs = _rttMs,
            JitterMs = _jitterMs,
            NetworkSimulationEnabled = _simEnabled,
            SimLatencyMs = _simLatency,
            SimJitterMs = _simJitter,
            SimLossPercent = _simLoss
        };
    }
}
