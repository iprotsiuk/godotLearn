// Scripts/Net/NetSession.Server.cs
using System.Collections.Generic;
using Godot;
using NetRunnerSlice.Player;

namespace NetRunnerSlice.Net;

public partial class NetSession
{
    private void ServerPeerConnected(int peerId)
    {
        if (_serverPlayers.ContainsKey(peerId))
        {
            return;
        }

        bool serverCharacterVisible = _mode != RunMode.ListenServer;
        PlayerCharacter character = CreateCharacter(peerId, false, serverCharacterVisible);
        EnsureServerPlayer(peerId, character);
    }

    private void TickServer(float delta)
    {
        _serverTick++;

        float fixedDt = 1.0f / _config.ServerTickRate;
        int maxSafeDelayTicks = NetConstants.MaxInputRedundancy - 1;
        int configuredDelayTicks = _config.ServerInputDelayTicks;
        int delayTicks = Mathf.Clamp(configuredDelayTicks, 0, maxSafeDelayTicks);
        uint delayTicksU = (uint)delayTicks;
        if (configuredDelayTicks != delayTicks && !_inputDelayClampWarned)
        {
            GD.PushWarning(
                $"ServerInputDelayTicks={configuredDelayTicks} exceeds redundancy window; clamped to {delayTicks} (MaxInputRedundancy={NetConstants.MaxInputRedundancy}).");
            _inputDelayClampWarned = true;
        }

        foreach (KeyValuePair<int, ServerPlayer> pair in _serverPlayers)
        {
            ServerPlayer player = pair.Value;
            InputCommand command;

            if (!player.HasStartedInputStream)
            {
                if (!player.HasReceivedAnyInput)
                {
                    command = player.LastInput;
                    command.MoveAxes = Vector2.Zero;
                    command.Buttons &= ~InputButtons.JumpPressed;
                    command.DtFixed = fixedDt;
                }
                else
                {
                    uint startSeq = player.LatestReceivedSeq > delayTicksU
                        ? player.LatestReceivedSeq - delayTicksU
                        : 1;
                    player.LastProcessedSeq = startSeq > 0 ? startSeq - 1 : 0;
                    player.HasStartedInputStream = true;
                }
            }

            uint expected = player.LastProcessedSeq + 1;
            if (player.Inputs.TryTakeExact(expected, out command) &&
                InputSanitizer.TrySanitizeServer(ref command, _config))
            {
                player.LastInput = command;
            }
            else
            {
                command = player.LastInput;
                command.Seq = expected;
                command.Buttons &= ~InputButtons.JumpPressed;
                command.DtFixed = fixedDt;
            }
            player.LastProcessedSeq = expected;

            player.Character.SetLook(command.Yaw, command.Pitch);
            PlayerMotor.Simulate(player.Character, command, _config);
        }

        if ((_serverTick % (uint)_snapshotEveryTicks) != 0)
        {
            return;
        }

        int stateCount = BuildSnapshotStates();
        NetCodec.WriteSnapshot(_snapshotPacket, _serverTick, _snapshotSendScratch.AsSpan(0, stateCount));

        foreach (int targetPeer in _serverPlayers.Keys)
        {
            if (_mode == RunMode.ListenServer && targetPeer == _localPeerId)
            {
                HandleSnapshot(_snapshotPacket);
                continue;
            }

            SendPacket(
                targetPeer,
                NetChannels.Snapshot,
                MultiplayerPeer.TransferModeEnum.UnreliableOrdered,
                _snapshotPacket);
        }
    }

    private int BuildSnapshotStates()
    {
        int count = 0;
        foreach (KeyValuePair<int, ServerPlayer> pair in _serverPlayers)
        {
            int peerId = pair.Key;
            ServerPlayer player = pair.Value;
            _snapshotSendScratch[count++] = new PlayerStateSnapshot
            {
                PeerId = peerId,
                LastProcessedSeqForThatClient = player.LastProcessedSeq,
                Pos = player.Character.GlobalPosition,
                Vel = player.Character.Velocity,
                Yaw = player.Character.Yaw,
                Pitch = player.Character.Pitch,
                Grounded = player.Character.Grounded
            };
        }

        return count;
    }

    private void HandleInputBundle(int fromPeer, byte[] packet)
    {
        if (!IsServer)
        {
            return;
        }

        if (!_serverPlayers.TryGetValue(fromPeer, out ServerPlayer? serverPlayer))
        {
            ServerPeerConnected(fromPeer);
            if (!_serverPlayers.TryGetValue(fromPeer, out serverPlayer))
            {
                return;
            }
        }

        if (!NetCodec.TryReadInputBundle(packet, _inputDecodeScratch, out int count))
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            InputCommand command = _inputDecodeScratch[i];
            if (serverPlayer.HasStartedInputStream && command.Seq <= serverPlayer.LastProcessedSeq)
            {
                continue;
            }

            if (!InputSanitizer.TrySanitizeServer(ref command, _config))
            {
                continue;
            }

            serverPlayer.Inputs.Push(command);
            serverPlayer.LatestReceivedSeq = command.Seq > serverPlayer.LatestReceivedSeq
                ? command.Seq
                : serverPlayer.LatestReceivedSeq;
            serverPlayer.HasReceivedAnyInput = true;
        }
    }
}
