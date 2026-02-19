// Scripts/Net/NetSession.Control.cs
using System.Collections.Generic;
using Godot;
using NetRunnerSlice.GameModes;
using NetRunnerSlice.Items;

namespace NetRunnerSlice.Net;

public partial class NetSession
{
    private const float ClientRttOutlierAbsoluteMs = 500.0f;
    private const float ClientRttOutlierDeviationScale = 6.0f;
    private const int ClientRttOutlierConsecutiveRequired = 3;
    private const float ServerRttOutlierAbsoluteMs = 250.0f;
    private const float ServerRttOutlierSlackMs = 50.0f;
    private const float ServerRttOutlierJitterScale = 4.0f;
    private const int ServerRttOutlierConsecutiveRequired = 3;
    private readonly System.Collections.Generic.Dictionary<int, int> _serverRttOutlierStreak = new();

    public void SetCurrentMatchState(MatchState state, bool broadcast = true)
    {
        MatchState sanitized = SanitizeMatchState(state);
        bool changed =
            CurrentMatchState.RoundIndex != sanitized.RoundIndex ||
            CurrentMatchState.Phase != sanitized.Phase ||
            CurrentMatchState.PhaseEndTick != sanitized.PhaseEndTick;
        CurrentMatchState = sanitized;
        if (broadcast && changed && IsServer)
        {
            BroadcastMatchStateToConnectedPeers();
        }
    }

    public void SetCurrentTagStateFull(TagState state, bool broadcast = true)
    {
        TagState sanitized = SanitizeTagState(state);
        bool changed =
            CurrentTagState.RoundIndex != sanitized.RoundIndex ||
            CurrentTagState.ItPeerId != sanitized.ItPeerId ||
            CurrentTagState.ItCooldownEndTick != sanitized.ItCooldownEndTick ||
            CurrentTagState.TagAppliedTick != sanitized.TagAppliedTick ||
            CurrentTagState.TaggerPeerId != sanitized.TaggerPeerId ||
            CurrentTagState.TaggedPeerId != sanitized.TaggedPeerId;
        CurrentTagState = sanitized;
        if (changed)
        {
            MarkTagProcessedForDiag(CurrentTagState.TagAppliedTick);
        }
        if (broadcast && changed && IsServer)
        {
            BroadcastTagStateFullToConnectedPeers();
        }
    }

    public void SetCurrentTagStateDelta(
        int itPeerId,
        uint itCooldownEndTick,
        uint tagAppliedTick,
        int taggerPeerId,
        int taggedPeerId,
        bool broadcast = true)
    {
        TagState sanitized = SanitizeTagState(new TagState
        {
            RoundIndex = CurrentTagState.RoundIndex,
            ItPeerId = itPeerId,
            ItCooldownEndTick = itCooldownEndTick,
            TagAppliedTick = tagAppliedTick,
            TaggerPeerId = taggerPeerId,
            TaggedPeerId = taggedPeerId
        });
        bool changed =
            CurrentTagState.ItPeerId != sanitized.ItPeerId ||
            CurrentTagState.ItCooldownEndTick != sanitized.ItCooldownEndTick ||
            CurrentTagState.TagAppliedTick != sanitized.TagAppliedTick ||
            CurrentTagState.TaggerPeerId != sanitized.TaggerPeerId ||
            CurrentTagState.TaggedPeerId != sanitized.TaggedPeerId;
        CurrentTagState = sanitized;
        if (changed)
        {
            MarkTagProcessedForDiag(CurrentTagState.TagAppliedTick);
        }
        if (broadcast && changed && IsServer)
        {
            BroadcastTagStateDeltaToConnectedPeers();
        }
    }

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
                        ServerPeerJoined?.Invoke(fromPeer);
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
                        _config.GroundStickVelocity,
                        _config.WallRunMaxTicks,
                        _config.SlideMaxTicks,
                        _config.WallRunGravityScale,
                        _config.WallJumpUpVelocity,
                        _config.WallJumpAwayVelocity);
                    SendPacket(fromPeer, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
                    GD.Print($"NetSession: Welcome sent to peer {fromPeer}");
                    SendMatchConfigToPeer(fromPeer);
                    SendMatchStateToPeer(fromPeer);
                    SendTagStateFullToPeer(fromPeer);
                    SendAllInventoryStatesToPeer(fromPeer);
                    SendAllPickupStatesToPeer(fromPeer);
                    SendAllFreezeStatesToPeer(fromPeer);
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
                _config.WallRunMaxTicks = Mathf.Clamp(NetCodec.ReadControlWallRunMaxTicks(packet), 0, 255);
                _config.SlideMaxTicks = Mathf.Clamp(NetCodec.ReadControlSlideMaxTicks(packet), 0, 255);
                _config.WallRunGravityScale = Mathf.Max(0.0f, NetCodec.ReadControlWallRunGravityScale(packet));
                _config.WallJumpUpVelocity = Mathf.Max(0.0f, NetCodec.ReadControlWallJumpUpVelocity(packet));
                _config.WallJumpAwayVelocity = Mathf.Max(0.0f, NetCodec.ReadControlWallJumpAwayVelocity(packet));
                _lastAuthoritativeServerTick = _server_sim_tick;
                GD.Print(
                    $"Welcome applied: MoveSpeed={_config.MoveSpeed:0.###}, GroundAccel={_config.GroundAcceleration:0.###}, InputDelayTicks={_config.ServerInputDelayTicks}");

                // Warmup ordering fix: seed the server tick estimate first, then choose first send tick near that
                // estimate so join warmup cannot backfill ancient ticks from a stale client_send_tick state.
                long welcomeUsec = (long)Time.GetTicksUsec();
                _netClock = new NetClock(_config.ServerTickRate);
                _netClock.ForceResync(_server_sim_tick, welcomeUsec);
                _client_est_server_tick = _netClock.GetEstimatedServerTick(welcomeUsec);
                _client_send_tick = 0;
                _pendingInputs.Clear();
                _pingSent.Clear();
                ClearLocalFirePressDiag();
                _nextInputSeq = 0;
                _lastAckedSeq = 0;
                _inputEpoch = 1;

                double welcomeNowSec = Time.GetTicksMsec() / 1000.0;
                _lastServerTickObsAtSec = welcomeNowSec;
                _clientWelcomeTimeSec = welcomeNowSec;
                _joinInitialInputDelayTicks = _appliedInputDelayTicks;
                _joinDelayGraceUntilSec = welcomeNowSec + ClientResyncJoinGraceSec;
                _delayTicksNextApplyAtSec = welcomeNowSec;
                _clientJoinDiagUntilSec = welcomeNowSec + 3.0;
                _clientNextJoinDiagAtSec = welcomeNowSec;
                _clientInputCmdsSentSinceLastDiag = 0;
                uint warmupStartTick = System.Math.Max(_server_sim_tick + 1u, _client_est_server_tick + 1u);
                if (_client_send_tick < warmupStartTick)
                {
                    _client_send_tick = warmupStartTick;
                }
                uint desired_horizon_tick = GetDesiredHorizonTick();
                int warmupSent = SendInputsUpToDesiredHorizon(desired_horizon_tick, allowPrediction: false);
                _clientInputCmdsSentSinceLastDiag += warmupSent;
                uint warmupEndTick = warmupSent > 0 ? _client_send_tick - 1 : warmupStartTick;
                GD.Print(
                    $"WarmupDiag: warmup_start_tick={warmupStartTick} warmup_end_tick={warmupEndTick} count_sent={warmupSent} " +
                    $"est_server_tick_at_welcome={_client_est_server_tick} welcome_server_tick={_server_sim_tick} " +
                    $"client_send_tick={_client_send_tick} desired_horizon_tick={desired_horizon_tick}");
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

                    bool hasBaseline = _rttMs > 0.01f;
                    float baselineRtt = Mathf.Max(0.0f, _rttMs);
                    float baselineDev = Mathf.Max(0.0f, _jitterMs);
                    float outlierThreshold = baselineRtt + (ClientRttOutlierDeviationScale * baselineDev);
                    bool outlier =
                        (hasBaseline && sampleRtt > outlierThreshold) ||
                        sampleRtt > ClientRttOutlierAbsoluteMs;
                    if (outlier)
                    {
                        _clientRttOutlierStreak++;
                        if (_clientRttOutlierStreak < ClientRttOutlierConsecutiveRequired)
                        {
                            GD.Print(
                                $"ClientPongOutlierIgnored: sample={sampleRtt:0.0}ms baseline={baselineRtt:0.0}/{baselineDev:0.0}ms " +
                                $"threshold={outlierThreshold:0.0}ms streak={_clientRttOutlierStreak}");
                        }
                        else
                        {
                            ApplyClientRttSample(sampleRtt);
                            _clientRttOutlierStreak = 0;
                        }
                    }
                    else
                    {
                        _clientRttOutlierStreak = 0;
                        ApplyClientRttSample(sampleRtt);
                    }

                    uint serverTick = NetCodec.ReadControlServerTick(packet);
                    _server_sim_tick = serverTick;
                    _lastAuthoritativeServerTick = serverTick;
                    _netClock?.ObserveServerTick(serverTick, (long)Time.GetTicksUsec());
                    _lastServerTickObsAtSec = nowSec;
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
                _lastServerTickObsAtSec = Time.GetTicksMsec() / 1000.0;
                TriggerClientResync("server_resync_hint", hintServerTick);
                break;
            case ControlType.MatchConfig:
                MatchConfig receivedConfig = SanitizeMatchConfig(NetCodec.ReadControlMatchConfig(packet));
                CurrentMatchConfig = receivedConfig;
                GD.Print($"NetSession: MatchConfig received mode={receivedConfig.ModeId} roundTimeSec={receivedConfig.RoundTimeSec}");
                MatchConfigReceived?.Invoke(receivedConfig);
                break;
            case ControlType.MatchState:
                MatchState receivedState = SanitizeMatchState(NetCodec.ReadControlMatchState(packet));
                CurrentMatchState = receivedState;
                GD.Print(
                    $"NetSession: MatchState received roundIndex={receivedState.RoundIndex} phase={receivedState.Phase} phaseEndTick={receivedState.PhaseEndTick}");
                MatchStateReceived?.Invoke(receivedState);
                break;
            case ControlType.TagStateFull:
                TagState fullState = SanitizeTagState(NetCodec.ReadControlTagStateFull(packet));
                GD.Print(
                    $"NetSession: TagStateFull received roundIndex={fullState.RoundIndex} itPeerId={fullState.ItPeerId} cooldownEndTick={fullState.ItCooldownEndTick} tagAppliedTick={fullState.TagAppliedTick} taggerPeerId={fullState.TaggerPeerId} taggedPeerId={fullState.TaggedPeerId}");
                QueueClientTagStateEvent(fullState, isFull: true);
                break;
            case ControlType.TagStateDelta:
                TagState deltaState = SanitizeTagState(new TagState
                {
                    RoundIndex = CurrentTagState.RoundIndex,
                    ItPeerId = NetCodec.ReadControlTagStateDeltaItPeer(packet),
                    ItCooldownEndTick = NetCodec.ReadControlTagStateDeltaCooldownEndTick(packet),
                    TagAppliedTick = NetCodec.ReadControlTagStateDeltaAppliedTick(packet),
                    TaggerPeerId = NetCodec.ReadControlTagStateDeltaTaggerPeer(packet),
                    TaggedPeerId = NetCodec.ReadControlTagStateDeltaTaggedPeer(packet)
                });
                GD.Print(
                    $"NetSession: TagStateDelta received itPeerId={deltaState.ItPeerId} cooldownEndTick={deltaState.ItCooldownEndTick} tagAppliedTick={deltaState.TagAppliedTick} taggerPeerId={deltaState.TaggerPeerId} taggedPeerId={deltaState.TaggedPeerId}");
                QueueClientTagStateEvent(deltaState, isFull: false);
                break;
            case ControlType.InventoryState:
                int peerId = NetCodec.ReadControlInventoryPeerId(packet);
                byte itemId = NetCodec.ReadControlInventoryItemId(packet);
                byte charges = NetCodec.ReadControlInventoryCharges(packet);
                uint cooldownEndTick = NetCodec.ReadControlInventoryCooldownEndTick(packet);
                _clientInventory[peerId] = (itemId, charges, cooldownEndTick);
                InventoryStateReceived?.Invoke(peerId);
                break;
            case ControlType.PickupState:
                int pickupId = NetCodec.ReadControlPickupStatePickupId(packet);
                bool isActive = NetCodec.ReadControlPickupStateIsActive(packet);
                if (isActive)
                {
                    _inactivePickups.Remove(pickupId);
                }
                else
                {
                    _inactivePickups.Add(pickupId);
                }

                if (_pickups.TryGetValue(pickupId, out PickupItem? pickup))
                {
                    pickup.SetActive(isActive);
                }
                break;
            case ControlType.FreezeState:
                int targetPeerId = NetCodec.ReadControlFreezeStateTargetPeerId(packet);
                uint frozenUntilTick = NetCodec.ReadControlFreezeStateFrozenUntilTick(packet);
                uint tickNow = _mode == RunMode.ListenServer ? _server_sim_tick : GetEstimatedServerTickNow();
                bool wasFrozen = IsFrozen(targetPeerId, tickNow);
                _freezeUntilTickByPeer[targetPeerId] = frozenUntilTick;
                if (targetPeerId == _localPeerId)
                {
                    bool frozenNow = IsFrozen(targetPeerId, tickNow);
                    if (frozenNow && !wasFrozen)
                    {
                        _frozenYaw = _lookYaw;
                        _frozenPitch = _lookPitch;
                        _localFreezeActive = true;
                    }
                    else if (!frozenNow)
                    {
                        _localFreezeActive = false;
                    }
                }
                break;
        }
    }

    private static MatchConfig SanitizeMatchConfig(in MatchConfig config)
    {
        GameModeId modeId = System.Enum.IsDefined(typeof(GameModeId), config.ModeId)
            ? config.ModeId
            : GameModeId.FreeRun;
        return new MatchConfig
        {
            ModeId = modeId,
            RoundTimeSec = Mathf.Clamp(config.RoundTimeSec, 30, 900)
        };
    }

    private static MatchState SanitizeMatchState(in MatchState state)
    {
        MatchPhase phase = System.Enum.IsDefined(typeof(MatchPhase), state.Phase)
            ? state.Phase
            : MatchPhase.Running;
        return new MatchState
        {
            RoundIndex = Mathf.Max(0, state.RoundIndex),
            Phase = phase,
            PhaseEndTick = state.PhaseEndTick
        };
    }

    private static TagState SanitizeTagState(in TagState state)
    {
        return new TagState
        {
            RoundIndex = Mathf.Max(0, state.RoundIndex),
            ItPeerId = state.ItPeerId,
            ItCooldownEndTick = state.ItCooldownEndTick,
            TagAppliedTick = state.TagAppliedTick,
            TaggerPeerId = state.TaggerPeerId,
            TaggedPeerId = state.TaggedPeerId
        };
    }

    private void SendMatchConfigToPeer(int peerId)
    {
        MatchConfig sanitized = SanitizeMatchConfig(CurrentMatchConfig);
        CurrentMatchConfig = sanitized;
        NetCodec.WriteControlMatchConfig(_controlPacket, sanitized);
        SendPacket(peerId, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
    }

    private void SendMatchStateToPeer(int peerId)
    {
        CurrentMatchState = SanitizeMatchState(CurrentMatchState);
        NetCodec.WriteControlMatchState(_controlPacket, CurrentMatchState);
        SendPacket(peerId, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
    }

    private void SendTagStateFullToPeer(int peerId)
    {
        CurrentTagState = SanitizeTagState(CurrentTagState);
        NetCodec.WriteControlTagStateFull(_controlPacket, CurrentTagState);
        SendPacket(peerId, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
    }

    private void SendTagStateDeltaToPeer(int peerId)
    {
        CurrentTagState = SanitizeTagState(CurrentTagState);
        NetCodec.WriteControlTagStateDelta(
            _controlPacket,
            CurrentTagState.ItPeerId,
            CurrentTagState.ItCooldownEndTick,
            CurrentTagState.TagAppliedTick,
            CurrentTagState.TaggerPeerId,
            CurrentTagState.TaggedPeerId);
        SendPacket(peerId, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
    }

    private void SendInventoryStateToPeer(int targetPeerId, int inventoryPeerId, byte itemId, byte charges, uint cooldownEndTick)
    {
        NetCodec.WriteControlInventoryState(_controlPacket, inventoryPeerId, itemId, charges, cooldownEndTick);
        SendPacket(targetPeerId, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
    }

    private void SendAllInventoryStatesToPeer(int targetPeerId)
    {
        foreach (KeyValuePair<int, ServerPlayer> pair in _serverPlayers)
        {
            int inventoryPeerId = pair.Key;
            ServerPlayer inventoryPlayer = pair.Value;
            SendInventoryStateToPeer(
                targetPeerId,
                inventoryPeerId,
                (byte)inventoryPlayer.EquippedItem,
                inventoryPlayer.EquippedCharges,
                inventoryPlayer.EquippedCooldownEndTick);
        }
    }

    private void SendPickupStateToPeer(int targetPeerId, int pickupId, bool active)
    {
        NetCodec.WriteControlPickupState(_controlPacket, pickupId, active);
        SendPacket(targetPeerId, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
    }

    private void SendAllPickupStatesToPeer(int targetPeerId)
    {
        foreach (int pickupId in _pickups.Keys)
        {
            bool active = !_inactivePickups.Contains(pickupId);
            SendPickupStateToPeer(targetPeerId, pickupId, active);
        }
    }

    private void SendFreezeStateToPeer(int targetPeerId, int freezePeerId, uint frozenUntilTick)
    {
        NetCodec.WriteControlFreezeState(_controlPacket, freezePeerId, frozenUntilTick);
        SendPacket(targetPeerId, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _controlPacket);
    }

    private void SendAllFreezeStatesToPeer(int targetPeerId)
    {
        foreach (KeyValuePair<int, uint> pair in _freezeUntilTickByPeer)
        {
            SendFreezeStateToPeer(targetPeerId, pair.Key, pair.Value);
        }
    }

    private void BroadcastMatchStateToConnectedPeers()
    {
        foreach (int peerId in _serverPlayers.Keys)
        {
            if (_mode == RunMode.ListenServer && peerId == _localPeerId)
            {
                continue;
            }

            SendMatchStateToPeer(peerId);
        }
    }

    private void BroadcastTagStateFullToConnectedPeers()
    {
        foreach (int peerId in _serverPlayers.Keys)
        {
            if (_mode == RunMode.ListenServer && peerId == _localPeerId)
            {
                continue;
            }

            SendTagStateFullToPeer(peerId);
        }
    }

    private void BroadcastTagStateDeltaToConnectedPeers()
    {
        foreach (int peerId in _serverPlayers.Keys)
        {
            if (_mode == RunMode.ListenServer && peerId == _localPeerId)
            {
                continue;
            }

            SendTagStateDeltaToPeer(peerId);
        }
    }

    private void BroadcastInventoryStateForPeer(int inventoryPeerId)
    {
        if (!IsServer || !_serverPlayers.TryGetValue(inventoryPeerId, out ServerPlayer? inventoryPlayer))
        {
            return;
        }

        foreach (int peerId in _serverPlayers.Keys)
        {
            if (_mode == RunMode.ListenServer && peerId == _localPeerId)
            {
                continue;
            }

            SendInventoryStateToPeer(
                peerId,
                inventoryPeerId,
                (byte)inventoryPlayer.EquippedItem,
                inventoryPlayer.EquippedCharges,
                inventoryPlayer.EquippedCooldownEndTick);
        }
    }

    private void BroadcastPickupState(int pickupId, bool active)
    {
        if (!IsServer)
        {
            return;
        }

        foreach (int peerId in _serverPlayers.Keys)
        {
            if (_mode == RunMode.ListenServer && peerId == _localPeerId)
            {
                continue;
            }

            SendPickupStateToPeer(peerId, pickupId, active);
        }
    }

    private void BroadcastFreezeStateForPeer(int freezePeerId, uint frozenUntilTick)
    {
        if (!IsServer)
        {
            return;
        }

        foreach (int peerId in _serverPlayers.Keys)
        {
            if (_mode == RunMode.ListenServer && peerId == _localPeerId)
            {
                continue;
            }

            SendFreezeStateToPeer(peerId, freezePeerId, frozenUntilTick);
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

        bool hasBaseline = serverPlayer.RttMs > 0.01f;
        float jitterBaseline = Mathf.Max(0.0f, serverPlayer.JitterMs);
        float outlierThreshold = serverPlayer.RttMs + (ServerRttOutlierJitterScale * jitterBaseline) + ServerRttOutlierSlackMs;
        bool outlier = hasBaseline &&
            sampleRtt > ServerRttOutlierAbsoluteMs &&
            sampleRtt > outlierThreshold;
        if (outlier)
        {
            int streak = 1;
            if (_serverRttOutlierStreak.TryGetValue(fromPeer, out int existing))
            {
                streak = existing + 1;
            }
            _serverRttOutlierStreak[fromPeer] = streak;

            if (streak < ServerRttOutlierConsecutiveRequired)
            {
                GD.Print(
                    $"ServerPongOutlierIgnored: peer={fromPeer} sample={sampleRtt:0.0}ms " +
                    $"baseline={serverPlayer.RttMs:0.0}/{serverPlayer.JitterMs:0.0}ms streak={streak}");
                UpdateEffectiveInputDelayForPeer(fromPeer, serverPlayer, sendDelayUpdate: true, nowSec);
                return;
            }
        }
        else
        {
            _serverRttOutlierStreak[fromPeer] = 0;
        }

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

        _serverRttOutlierStreak[fromPeer] = 0;

        UpdateEffectiveInputDelayForPeer(fromPeer, serverPlayer, sendDelayUpdate: true, nowSec);
    }

    private void ApplyClientRttSample(float sampleRtt)
    {
        if (_rttMs <= 0.01f)
        {
            _rttMs = sampleRtt;
            _jitterMs = 0.0f;
            return;
        }

        float deltaRtt = Mathf.Abs(sampleRtt - _rttMs);
        _rttMs = Mathf.Lerp(_rttMs, sampleRtt, NetConstants.RttEwmaAlpha);
        _jitterMs = Mathf.Lerp(_jitterMs, deltaRtt, NetConstants.RttEwmaAlpha);
    }
}
