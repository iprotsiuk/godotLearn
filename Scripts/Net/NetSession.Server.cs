// Scripts/Net/NetSession.Server.cs
using System.Collections.Generic;
using Godot;
using NetRunnerSlice.Player;

namespace NetRunnerSlice.Net;

public partial class NetSession
{
    private int ComputeWanDelayTicks(float rttMs)
    {
        float tickMs = 1000.0f / Mathf.Max(1, _config.ServerTickRate);
        float oneWayMs = Mathf.Max(0.0f, rttMs) * 0.5f;
        int delayTicks = Mathf.CeilToInt((oneWayMs + NetConstants.WanInputSafetyMs) / tickMs);
        return Mathf.Clamp(delayTicks, NetConstants.MinWanInputDelayTicks, NetConstants.MaxWanInputDelayTicks);
    }

    private int GetEffectiveInputDelayTicksForPeer(int peerId, ServerPlayer player)
    {
        if (_mode == RunMode.ListenServer && peerId == _localPeerId)
        {
            return 0;
        }

        float rttMs = player.RttMs > 0.01f
            ? player.RttMs
            : NetConstants.WanDefaultRttMs;
        return ComputeWanDelayTicks(rttMs);
    }

    private void UpdateEffectiveInputDelayForPeer(int peerId, ServerPlayer player, bool sendDelayUpdate)
    {
        int nextDelayTicks = GetEffectiveInputDelayTicksForPeer(peerId, player);
        if (nextDelayTicks == player.EffectiveInputDelayTicks)
        {
            return;
        }

        player.EffectiveInputDelayTicks = nextDelayTicks;
        if (player.ExpectedInputTickInitialized)
        {
            uint minExpected = _serverTick + (uint)Mathf.Max(0, nextDelayTicks);
            if (player.ExpectedInputTick < minExpected)
            {
                player.ExpectedInputTick = minExpected;
            }
        }

        if (!sendDelayUpdate || (_mode == RunMode.ListenServer && peerId == _localPeerId))
        {
            return;
        }

        NetCodec.WriteControlDelayUpdate(_controlPacket, nextDelayTicks);
        SendPacket(peerId, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
    }

    private void EnsureExpectedInputTickInitialized(ServerPlayer player)
    {
        if (player.ExpectedInputTickInitialized)
        {
            return;
        }

        player.ExpectedInputTick = _serverTick + (uint)Mathf.Max(0, player.EffectiveInputDelayTicks);
        player.ExpectedInputTickInitialized = true;
    }

    private static InputCommand BuildNeutralInput(in InputCommand basis, uint inputTick, float fixedDt, uint epoch)
    {
        InputCommand neutral = basis;
        neutral.InputTick = inputTick;
        neutral.InputEpoch = epoch;
        neutral.MoveAxes = Vector2.Zero;
        neutral.Buttons = InputButtons.None;
        neutral.DtFixed = fixedDt;
        return neutral;
    }

    private void ResetPeerForEpoch(int peerId, ServerPlayer player, uint epoch)
    {
        player.Inputs.Clear();
        player.CurrentInputEpoch = epoch == 0 ? 1u : epoch;
        player.MissingInputTicks = 0;
        player.PendingSafetyNeutralTicks = 1;
        UpdateEffectiveInputDelayForPeer(peerId, player, sendDelayUpdate: false);
        player.ExpectedInputTick = _serverTick + (uint)Mathf.Max(0, player.EffectiveInputDelayTicks);
        player.ExpectedInputTickInitialized = true;
        player.LastInput = BuildNeutralInput(player.LastInput, player.ExpectedInputTick, 1.0f / _config.ServerTickRate, player.CurrentInputEpoch);
    }

    private void SendServerPingIfDue(int peerId, ServerPlayer player, double nowSec)
    {
        if (_mode == RunMode.ListenServer && peerId == _localPeerId)
        {
            return;
        }

        if (nowSec < player.NextPingAtSec)
        {
            return;
        }

        player.NextPingSeq++;
        if (player.NextPingSeq == 0)
        {
            player.NextPingSeq = 1;
        }

        player.PendingPings[player.NextPingSeq] = nowSec;
        if (player.PendingPings.Count > NetConstants.MaxOutstandingPings)
        {
            ushort oldestSeq = 0;
            bool found = false;
            foreach (ushort seq in player.PendingPings.Keys)
            {
                oldestSeq = seq;
                found = true;
                break;
            }

            if (found)
            {
                player.PendingPings.Remove(oldestSeq);
            }
        }

        uint nowMs = (uint)Time.GetTicksMsec();
        NetCodec.WriteControlPing(_controlPacket, player.NextPingSeq, nowMs);
        SendPacket(peerId, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
        player.NextPingAtSec = nowSec + NetConstants.PingIntervalSec;
    }

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
        double nowSec = Time.GetTicksMsec() / 1000.0;

        foreach (KeyValuePair<int, ServerPlayer> pair in _serverPlayers)
        {
            int peerId = pair.Key;
            ServerPlayer player = pair.Value;
            UpdateEffectiveInputDelayForPeer(peerId, player, sendDelayUpdate: true);
            EnsureExpectedInputTickInitialized(player);
            SendServerPingIfDue(peerId, player, nowSec);

            uint neededTick = player.ExpectedInputTick;
            InputCommand command;
            bool usedBufferedInput = false;

            if (player.PendingSafetyNeutralTicks > 0)
            {
                command = BuildNeutralInput(player.LastInput, neededTick, fixedDt, player.CurrentInputEpoch);
                player.LastInput = command;
                player.PendingSafetyNeutralTicks--;
                player.MissingInputTicks = NetConstants.HoldLastInputTicks + 1;
            }
            else if (player.Inputs.TryTake(neededTick, out command) &&
                     InputSanitizer.TrySanitizeServer(ref command, _config))
            {
                command.InputTick = neededTick;
                command.InputEpoch = player.CurrentInputEpoch;
                player.LastInput = command;
                player.LastProcessedSeq = command.Seq;
                player.MissingInputTicks = 0;
                usedBufferedInput = true;
            }
            else if (player.MissingInputTicks < NetConstants.HoldLastInputTicks)
            {
                command = player.LastInput;
                command.InputTick = neededTick;
                command.InputEpoch = player.CurrentInputEpoch;
                command.Buttons &= ~InputButtons.JumpPressed;
                command.DtFixed = fixedDt;
                player.MissingInputTicks++;
            }
            else
            {
                command = BuildNeutralInput(player.LastInput, neededTick, fixedDt, player.CurrentInputEpoch);
                player.LastInput = command;
                player.MissingInputTicks++;
            }

            if (!usedBufferedInput)
            {
                command.DtFixed = fixedDt;
            }

            player.ExpectedInputTick = neededTick + 1;

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
            if (!InputSanitizer.TrySanitizeServer(ref command, _config))
            {
                continue;
            }

            if (command.InputEpoch != serverPlayer.CurrentInputEpoch)
            {
                ResetPeerForEpoch(fromPeer, serverPlayer, command.InputEpoch);
            }

            EnsureExpectedInputTickInitialized(serverPlayer);
            uint expectedInputTick = serverPlayer.ExpectedInputTick;
            if (command.InputTick < expectedInputTick)
            {
                continue;
            }

            uint maxFutureInputTick = expectedInputTick + (uint)NetConstants.MaxFutureInputTicks;
            if (command.InputTick > maxFutureInputTick)
            {
                continue;
            }

            serverPlayer.Inputs.Store(command);
        }
    }
}
