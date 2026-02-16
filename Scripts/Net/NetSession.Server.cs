// Scripts/Net/NetSession.Server.cs
using System.Collections.Generic;
using Godot;
using NetRunnerSlice.Player;

namespace NetRunnerSlice.Net;

public partial class NetSession
{
    private static void IncrementMissingInputStreak(ServerPlayer player)
    {
        player.MissingInputStreakCurrent++;
        if (player.MissingInputStreakCurrent > player.MissingInputStreakMax)
        {
            player.MissingInputStreakMax = player.MissingInputStreakCurrent;
        }
    }

    private void LogServerDiagnosticsIfDue(double nowSec)
    {
        if (nowSec < _nextServerDiagnosticsLogAtSec)
        {
            return;
        }

        _nextServerDiagnosticsLogAtSec = nowSec + ServerDiagnosticsLogIntervalSec;
        foreach (KeyValuePair<int, ServerPlayer> pair in _serverPlayers)
        {
            int peerId = pair.Key;
            ServerPlayer player = pair.Value;
            ulong totalUsage = (ulong)player.TicksUsedBufferedInput + player.TicksUsedHoldLast + player.TicksUsedNeutral;
            float bufferedPct = totalUsage == 0
                ? 0.0f
                : (100.0f * player.TicksUsedBufferedInput) / totalUsage;

            GD.Print(
                $"ServerWANDiag: peer={peerId} delayTicks={player.EffectiveInputDelayTicks} rtt={player.RttMs:0.0}ms jitter={player.JitterMs:0.0}ms " +
                $"drops(old/future)={player.DroppedOldInputCount}/{player.DroppedFutureInputCount} " +
                $"usage(buffered/hold/neutral)={player.TicksUsedBufferedInput}/{player.TicksUsedHoldLast}/{player.TicksUsedNeutral} bufferedPct={bufferedPct:0.0}% " +
                $"missingStreak(cur/max)={player.MissingInputStreakCurrent}/{player.MissingInputStreakMax}");
        }
    }

    private int ComputeWanDelayTicks(float rttMs, float jitterMs)
    {
        float tickMs = 1000.0f / Mathf.Max(1, _config.ServerTickRate);
        float oneWayMs = Mathf.Max(0.0f, rttMs) * 0.5f;
        float totalMs = oneWayMs + NetConstants.WanInputSafetyMs + (Mathf.Max(0.0f, jitterMs) * NetConstants.WanInputJitterScale);
        int delayTicks = Mathf.CeilToInt(totalMs / tickMs);
        int maxAllowedDelay = Mathf.Max(0, NetConstants.MaxFutureInputTicks - 2);
        int clampedMax = Mathf.Min(NetConstants.MaxWanInputDelayTicks, maxAllowedDelay);
        return Mathf.Clamp(delayTicks, NetConstants.MinWanInputDelayTicks, clampedMax);
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
        return ComputeWanDelayTicks(rttMs, player.JitterMs);
    }

    private void UpdateEffectiveInputDelayForPeer(int peerId, ServerPlayer player, bool sendDelayUpdate, double nowSec = -1.0)
    {
        if (nowSec < 0.0)
        {
            nowSec = Time.GetTicksMsec() / 1000.0;
        }

        int currentDelayTicks = player.EffectiveInputDelayTicks;
        int targetDelayTicks = GetEffectiveInputDelayTicksForPeer(peerId, player);
        targetDelayTicks = Mathf.Clamp(
            targetDelayTicks,
            NetConstants.MinWanInputDelayTicks,
            Mathf.Min(NetConstants.MaxWanInputDelayTicks, Mathf.Max(0, NetConstants.MaxFutureInputTicks - 2)));

        if (nowSec < player.NextDelayUpdateAtSec)
        {
            return;
        }

        player.NextDelayUpdateAtSec = nowSec + NetConstants.InputDelayUpdateIntervalSec;
        int delta = targetDelayTicks - currentDelayTicks;
        int nextDelayTicks = currentDelayTicks;
        if (delta > 0)
        {
            nextDelayTicks++;
        }
        else if (delta < 0)
        {
            nextDelayTicks--;
        }

        nextDelayTicks = Mathf.Clamp(
            nextDelayTicks,
            NetConstants.MinWanInputDelayTicks,
            Mathf.Min(NetConstants.MaxWanInputDelayTicks, Mathf.Max(0, NetConstants.MaxFutureInputTicks - 2)));
        if (nextDelayTicks == currentDelayTicks)
        {
            return;
        }

        player.EffectiveInputDelayTicks = nextDelayTicks;
        if (!sendDelayUpdate || (_mode == RunMode.ListenServer && peerId == _localPeerId))
        {
            return;
        }

        NetCodec.WriteControlDelayUpdate(_controlPacket, player.EffectiveInputDelayTicks);
        SendPacket(peerId, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
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
        uint neededTick = _serverTick + 1;
        player.LastInput = BuildNeutralInput(player.LastInput, neededTick, 1.0f / _config.ServerTickRate, player.CurrentInputEpoch);
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
            UpdateEffectiveInputDelayForPeer(peerId, player, sendDelayUpdate: true, nowSec);
            SendServerPingIfDue(peerId, player, nowSec);

            uint neededTick = _serverTick;
            InputCommand command;
            bool usedBufferedInput = false;
            bool usedHoldLast = false;
            bool usedNeutral = false;

            if (player.PendingSafetyNeutralTicks > 0)
            {
                command = BuildNeutralInput(player.LastInput, neededTick, fixedDt, player.CurrentInputEpoch);
                player.LastInput = command;
                player.PendingSafetyNeutralTicks--;
                player.MissingInputTicks = NetConstants.HoldLastInputTicks + 1;
                usedNeutral = true;
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
                usedHoldLast = true;
            }
            else
            {
                command = BuildNeutralInput(player.LastInput, neededTick, fixedDt, player.CurrentInputEpoch);
                player.LastInput = command;
                player.MissingInputTicks++;
                usedNeutral = true;
            }

            if (usedBufferedInput)
            {
                player.TicksUsedBufferedInput++;
                player.MissingInputStreakCurrent = 0;
            }
            else if (usedHoldLast)
            {
                player.TicksUsedHoldLast++;
                IncrementMissingInputStreak(player);
            }
            else if (usedNeutral)
            {
                player.TicksUsedNeutral++;
                IncrementMissingInputStreak(player);
            }

            if (!usedBufferedInput)
            {
                command.DtFixed = fixedDt;
            }

            if (player.MissingInputStreakCurrent > NetConstants.MaxMissingBeforeResyncHint &&
                nowSec >= player.NextResyncHintAtSec &&
                !(_mode == RunMode.ListenServer && peerId == _localPeerId))
            {
                NetCodec.WriteControlResyncHint(_controlPacket, _serverTick);
                SendPacket(peerId, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
                player.NextResyncHintAtSec = nowSec + 0.5;
            }

            player.Character.SetLook(command.Yaw, command.Pitch);
            PlayerMotor.Simulate(player.Character, command, _config);
        }

        LogServerDiagnosticsIfDue(nowSec);
        RecordRewindFrame(_serverTick);

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
                Grounded = player.Character.Grounded,
                DroppedOldInputCount = player.DroppedOldInputCount,
                DroppedFutureInputCount = player.DroppedFutureInputCount,
                TicksUsedBufferedInput = player.TicksUsedBufferedInput,
                TicksUsedHoldLast = player.TicksUsedHoldLast,
                TicksUsedNeutral = player.TicksUsedNeutral,
                MissingInputStreakCurrent = player.MissingInputStreakCurrent,
                MissingInputStreakMax = player.MissingInputStreakMax,
                EffectiveDelayTicks = player.EffectiveInputDelayTicks,
                ServerPeerRttMs = player.RttMs,
                ServerPeerJitterMs = player.JitterMs
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

            uint maxFutureInputTick = _serverTick + (uint)NetConstants.MaxFutureInputTicks;
            if (command.InputTick > maxFutureInputTick)
            {
                serverPlayer.DroppedFutureInputCount++;
                continue;
            }

            uint minAllowedTick = _serverTick > (uint)NetConstants.MaxPastInputTicks
                ? _serverTick - (uint)NetConstants.MaxPastInputTicks
                : 0;
            if (command.InputTick < minAllowedTick)
            {
                serverPlayer.DroppedOldInputCount++;
                continue;
            }

            serverPlayer.Inputs.Store(command);
        }
    }
}
