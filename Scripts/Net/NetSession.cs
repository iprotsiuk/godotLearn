// Scripts/Net/NetSession.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using NetRunnerSlice.GameModes;
using NetRunnerSlice.Items;
using NetRunnerSlice.Player;

namespace NetRunnerSlice.Net;

public partial class NetSession : Node
{
    private const string PlayerCharacterScenePath = "res://Scenes/Player/PlayerCharacter.tscn";
    private const int RewindHistoryTicks = 120;
    private const double ServerDiagnosticsLogIntervalSec = 2.0;
    private const double JoinDiagnosticsLogIntervalSec = 0.25;
    private const double ClientResyncJoinGraceSec = 2.0;
    private const int ClientInputSafetyTicks = 2;
    private const float WeaponMaxRange = 200.0f;
    private const float WeaponTargetRadius = 0.5f;
    private const float WeaponOriginMaxOffset = 1.5f;
    private const int MaxPlayerHealth = 100;
    private const int WeaponHitDamage = 19;
    private const int SpecialHitPeerId = -2;
    private const float SimNetHitchThresholdMs = 45.0f;

    private enum RunMode
    {
        None,
        ListenServer,
        DedicatedServer,
        Client
    }

    private enum ClientFocusMode
    {
        Focused,
        Unfocused
    }

    private sealed class ServerPlayer
    {
        public required PlayerCharacter Character;
        public ServerInputBuffer Inputs { get; } = new();
        public readonly Dictionary<ushort, double> PendingPings = new();
        public uint CurrentInputEpoch = 1;
        public int MissingInputTicks;
        public int PendingSafetyNeutralTicks;
        public float RttMs = NetConstants.WanDefaultRttMs;
        public float JitterMs;
        public int EffectiveInputDelayTicks;
        public ushort NextPingSeq;
        public double NextPingAtSec;
        public double NextDelayUpdateAtSec;
        public double NextResyncHintAtSec;
        public uint LastProcessedSeq;
        public InputCommand LastInput;
        public uint DroppedOldInputCount;
        public uint DroppedFutureInputCount;
        public uint TicksUsedBufferedInput;
        public uint TicksUsedHoldLast;
        public uint TicksUsedNeutral;
        public uint MissingInputStreakCurrent;
        public uint MissingInputStreakMax;
        public int HealthCurrent = MaxPlayerHealth;
        public int HealthMax = MaxPlayerHealth;
        public ItemId EquippedItem = ItemId.None;
        public byte EquippedCharges;
        public uint EquippedCooldownEndTick;
        public readonly byte[] UsageWindow = new byte[120];
        public int UsageWindowWriteIndex;
        public int UsageWindowCount;
        public int UsageWindowBuffered;
        public int UsageWindowHold;
        public int UsageWindowNeutral;
        public double JoinDelayGraceUntilSec;
        public double JoinDiagUntilSec;
        public double NextJoinDiagAtSec;
    }

    private sealed class RemoteEntity
    {
        public required PlayerCharacter Character;
        public RemoteSnapshotBuffer Buffer { get; } = new();
    }

    private struct RewindSample
    {
        public int PeerId;
        public Vector3 Position;
    }

    private struct PendingTagStateEvent
    {
        public TagState State;
        public bool IsFull;
    }

    private readonly uint[] _rewindTicks = new uint[RewindHistoryTicks];
    private readonly int[] _rewindCounts = new int[RewindHistoryTicks];
    private readonly RewindSample[,] _rewindSamples = new RewindSample[RewindHistoryTicks, NetConstants.MaxPlayers];
    private readonly byte[] _fireResultPacket = new byte[NetConstants.FireResultPacketBytes];
    private readonly byte[] _fireVisualPacket = new byte[NetConstants.FireVisualPacketBytes];
    private readonly List<(PlayerCharacter Character, Vector3 Position)> _rewindRestoreScratch = new(NetConstants.MaxPlayers);
    private readonly List<(Node3D Node, double ExpireAt)> _debugDrawNodes = new(32);

    private readonly Dictionary<int, ServerPlayer> _serverPlayers = new();
    private readonly Dictionary<int, PlayerCharacter> _serverCharactersByPeer = new();
    private readonly Dictionary<int, RemoteEntity> _remotePlayers = new();
    private readonly Dictionary<int, (byte ItemId, byte Charges, uint CooldownEndTick)> _clientInventory = new();
    private readonly Dictionary<int, PickupItem> _pickups = new();
    private readonly HashSet<int> _inactivePickups = new();
    private readonly Dictionary<int, uint> _pickupRespawnTickById = new();
    private readonly Dictionary<int, uint> _freezeUntilTickByPeer = new();
    private readonly Dictionary<int, TagDroneState> _clientTagDroneStatesByRunner = new();
    private readonly SortedDictionary<uint, PendingTagStateEvent> _pendingTagStateEventsByTick = new();
    private readonly List<int> _pickupRespawnReadyScratch = new();
    private readonly byte[] _inputPacket = new byte[NetConstants.InputPacketBytes];
    private readonly byte[] _snapshotPacket = new byte[NetConstants.SnapshotPacketBytes];
    private readonly byte[] _controlPacket = new byte[NetConstants.ControlPacketBytes];
    private readonly byte[] _tagDroneStatePacket = new byte[NetConstants.TagDroneStatePacketBytes];
    private readonly InputCommand[] _inputDecodeScratch = new InputCommand[NetConstants.MaxInputRedundancy];
    private readonly InputCommand[] _inputSendScratch = new InputCommand[NetConstants.MaxInputRedundancy];
    private readonly PlayerStateSnapshot[] _snapshotDecodeScratch = new PlayerStateSnapshot[NetConstants.MaxPlayers];
    private readonly PlayerStateSnapshot[] _snapshotSendScratch = new PlayerStateSnapshot[NetConstants.MaxPlayers];
    private RunMode _mode;
    private NetworkConfig _config = new();
    private PackedScene? _playerCharacterScene;
    private Node3D? _playerRoot;
    private bool _welcomeReceived;
    private bool _hasSpawnOrigin;
    private Transform3D _spawnOrigin = Transform3D.Identity;
    private float _spawnYaw;
    private SceneMultiplayer? _sceneMultiplayer;
    private NetworkSimulator? _simulator;
    private NetClock? _netClock;
    // Tick clock: server_sim_tick (authoritative simulation tick counter on server).
    private uint _server_sim_tick;
    // Tick clock: client_est_server_tick (client estimate of current server_sim_tick "now").
    private uint _client_est_server_tick;
    // Tick clock: client_send_tick (next tick index for generated/sent input commands).
    private uint _client_send_tick;
    private uint _lastAuthoritativeServerTick;
    private int _localPeerId;
    private int _snapshotEveryTicks = 3;
    private bool _simEnabled;
    private int _simLatency;
    private int _simJitter;
    private float _simLoss;
    private int _simSeed;
    private float _lookYaw;
    private float _lookPitch;
    private float _frozenYaw;
    private float _frozenPitch;
    private bool _localFreezeActive;
    private float _lastCorrectionMeters;
    private float _lastCorrectionXZMeters;
    private float _lastCorrectionYMeters;
    private float _lastCorrection3DMeters;
    private float _correctionsPerSec;
    private double _correctionRateWindowStartSec;
    private uint _correctionRateWindowCount;
    private float _rttMs;
    private float _jitterMs;
    private int _clientRttOutlierStreak;
    private bool _logControlPackets;
    private float _dynamicInterpolationDelayMs;
    private int _globalInterpDelayTicks;
    private int _interpUnderflowExtraTicks;
    private double _nextInterpDelayStepAtSec;
    private double _nextInterpUnderflowAdjustAtSec;
    private float _sessionSnapshotJitterEwmaMs;
    private double _lastSnapshotArrivalTimeSec;
    private bool _hasSnapshotArrivalTimeSec;
    private double _lastAuthoritativeSnapshotAtSec;
    private double _lastServerTickObsAtSec;
    private double _nextHardResyncAllowedAtSec;
    private int _tickDriftGuardBreachCount;
    private float _snapshotAgeMs;
    private ushort _pingSeq;
    private double _nextPingTimeSec;
    private readonly Dictionary<ushort, double> _pingSent = new();
    private double _nextServerDiagnosticsLogAtSec;
    private bool _hasFocus = true;
    private ClientFocusMode _clientFocusMode = ClientFocusMode.Focused;
    private bool _focusOutPending;
    private bool _focusOutResetApplied;
    private double _focusOutStartedAtSec;
    private double _lastNetPollAtSec;
    private double _lastPacketProcessedAtSec;
    private double _lastInputSendAtSec;
    private double _lastSnapshotAppliedAtSec;
    private int _savedMaxFpsBeforeUnfocus = -1;
    private double _clientTickAccumulatorSec;
    private double _nextFrameHitchLogAtSec;
    private bool _hasLastLocalAuthoritativeSnapshot;
    private PlayerStateSnapshot _lastLocalAuthoritativeSnapshot;
    private uint _lastLocalAuthoritativeServerTick;
    private double _realtimeStallWindowStartSec;
    private float _realtimeStallWindowMaxMs;
    private float _realtimeStallMs;
    private uint _hardResetCount;
    private string _lastHardResetReason = "none";
    private uint _lastAppliedTagEventTick;
    private bool _tagProcessedDiagThisFrame;
    private uint _tagProcessedDiagTick;
    private int _tagProcessedDiagCountThisFrame;
    private InputHistoryBuffer _pendingInputs = new();
    private uint _nextInputSeq;
    private uint _lastAckedSeq;
    private int _appliedInputDelayTicks;
    private int _targetInputDelayTicks;
    private double _delayTicksNextApplyAtSec;
    private double _joinDelayGraceUntilSec;
    private int _joinInitialInputDelayTicks;
    private double _clientWelcomeTimeSec;
    private double _clientJoinDiagUntilSec;
    private double _clientNextJoinDiagAtSec;
    private int _clientInputCmdsSentSinceLastDiag;
    private uint _inputEpoch = 1;
    private uint _serverDroppedOldInputCount;
    private uint _serverDroppedFutureInputCount;
    private uint _serverTicksUsedBufferedInput;
    private uint _serverTicksUsedHoldLast;
    private uint _serverTicksUsedNeutral;
    private uint _serverMissingInputStreakCurrent;
    private uint _serverMissingInputStreakMax;
    private int _serverEffectiveDelayTicks = -1;
    private float _serverPeerRttMs = -1.0f;
    private float _serverPeerJitterMs = -1.0f;
    private int _tickErrorTicks;
    private float _dropFutureRatePerSec;
    private double _dropFutureRateWindowStartSec;
    private uint _dropFutureRateWindowCount;
    private bool _resyncTriggered;
    private uint _resyncCount;
    private uint _resyncSuppressedDuringJoinCount;
    private double _nextResyncDiagLogAtSec;
    private int _localHealth = MaxPlayerHealth;
    private int _localHealthMax = MaxPlayerHealth;
    private PlayerCharacter? _localCharacter;
    public bool IsServer => _mode == RunMode.ListenServer || _mode == RunMode.DedicatedServer;
    public bool IsClient => _mode == RunMode.ListenServer || _mode == RunMode.Client;
    public int TickRate => Mathf.Max(1, _config.ServerTickRate);
    public float MoveSpeed => _config.MoveSpeed;
    public Vector3 SpawnOriginPosition => _hasSpawnOrigin ? _spawnOrigin.Origin : new Vector3(0.0f, 2.0f, 0.0f);
    public Node3D? PlayerRoot => _playerRoot;
    public SessionMetrics Metrics { get; private set; }
    public MatchConfig CurrentMatchConfig { get; set; } = new()
    {
        ModeId = GameModeId.FreeRun,
        RoundTimeSec = 120
    };
    public MatchState CurrentMatchState { get; private set; } = new()
    {
        RoundIndex = 0,
        Phase = MatchPhase.Running,
        PhaseEndTick = 0
    };
    public TagState CurrentTagState { get; private set; } = new()
    {
        RoundIndex = 0,
        ItPeerId = -1,
        ItCooldownEndTick = 0,
        TagAppliedTick = 0,
        TaggerPeerId = -1,
        TaggedPeerId = -1
    };
    public int LocalPeerId => _localPeerId;
    public event System.Action<MatchConfig>? MatchConfigReceived;
    public event System.Action<MatchState>? MatchStateReceived;
    public event System.Action<TagState>? TagStateFullReceived;
    public event System.Action<TagState>? TagStateDeltaReceived;
    public event System.Action<int>? InventoryStateReceived;
    public event System.Action<int>? ServerPeerJoined;
    public event System.Action<int>? ServerPeerLeft;
    public event System.Action<int, PlayerCharacter, InputCommand, uint>? ServerPostSimulatePlayer;
    /// <summary>
    /// Authoritative server-side characters only. Listen-server has duplicate bodies;
    /// do not use scene scanning for gameplay rules.
    /// </summary>
    public IReadOnlyDictionary<int, PlayerCharacter> ServerPlayers => _serverCharactersByPeer;
    public IReadOnlyDictionary<int, (byte ItemId, byte Charges, uint CooldownEndTick)> ClientInventory => _clientInventory;
    public int ServerPeerCount => _serverPlayers.Count;
    public System.Func<int, bool>? ServerCanPickupItem;
    public delegate bool ServerFreezeGunShotHandler(
        int shooterPeerId,
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        uint tick,
        out Vector3 hitPoint);
    public ServerFreezeGunShotHandler? ServerTryHandleFreezeGunShot;

    public int[] GetServerPeerIds()
    {
        int[] peers = new int[_serverPlayers.Count];
        int index = 0;
        foreach (int peerId in _serverPlayers.Keys)
        {
            peers[index++] = peerId;
        }

        return peers;
    }

    public int[] GetKnownPeerIds()
    {
        if (IsServer)
        {
            return GetServerPeerIds();
        }

        List<int> peers = new(_remotePlayers.Count + 1);
        if (_localPeerId > 0)
        {
            peers.Add(_localPeerId);
        }

        foreach (int peerId in _remotePlayers.Keys)
        {
            if (!peers.Contains(peerId))
            {
                peers.Add(peerId);
            }
        }

        return peers.ToArray();
    }

    public void FillKnownPeerIds(List<int> peers)
    {
        peers.Clear();
        if (IsServer)
        {
            foreach (int peerId in _serverPlayers.Keys)
            {
                peers.Add(peerId);
            }

            return;
        }

        if (_localPeerId > 0)
        {
            peers.Add(_localPeerId);
        }

        foreach (int peerId in _remotePlayers.Keys)
        {
            if (peerId == _localPeerId)
            {
                continue;
            }

            peers.Add(peerId);
        }
    }

    /// <summary>
    /// Returns the authoritative server-side character for a peer. Listen-server has duplicate bodies;
    /// do not use scene scanning for gameplay rules.
    /// </summary>
    public PlayerCharacter? GetServerCharacter(int peerId)
    {
        if (!IsServer)
        {
            return null;
        }

        if (!_serverPlayers.TryGetValue(peerId, out ServerPlayer? serverPlayer))
        {
            return null;
        }

        // Keep the convenience cache aligned with authoritative server-player state.
        _serverCharactersByPeer[peerId] = serverPlayer.Character;
        return serverPlayer.Character;
    }

    public bool TryGetServerCharacter(int peerId, out PlayerCharacter character)
    {
        character = null!;
        if (!IsServer)
        {
            return false;
        }

        if (_serverPlayers.TryGetValue(peerId, out ServerPlayer? serverPlayer))
        {
            character = serverPlayer.Character;
            // Keep the convenience cache aligned with authoritative server-player state.
            _serverCharactersByPeer[peerId] = serverPlayer.Character;
            return true;
        }

        return false;
    }

    public bool TryGetAnyCharacter(int peerId, out PlayerCharacter character)
    {
        character = null!;
        if (TryGetServerCharacter(peerId, out PlayerCharacter serverCharacter))
        {
            character = serverCharacter;
            return true;
        }

        if (_localCharacter is not null && peerId == _localPeerId)
        {
            character = _localCharacter;
            return true;
        }

        if (_remotePlayers.TryGetValue(peerId, out RemoteEntity? remote))
        {
            character = remote.Character;
            return true;
        }

        return false;
    }

    public bool TryGetClientInventory(int peerId, out byte itemId, out byte charges, out uint cooldownEndTick)
    {
        itemId = 0;
        charges = 0;
        cooldownEndTick = 0;
        if (!_clientInventory.TryGetValue(peerId, out (byte ItemId, byte Charges, uint CooldownEndTick) state))
        {
            return false;
        }

        itemId = state.ItemId;
        charges = state.Charges;
        cooldownEndTick = state.CooldownEndTick;
        return true;
    }

    public void RegisterPickup(PickupItem item)
    {
        _pickups[item.PickupId] = item;
        item.SetActive(!_inactivePickups.Contains(item.PickupId));
    }

    public void UnregisterPickup(int pickupId)
    {
        _pickups.Remove(pickupId);
        _inactivePickups.Remove(pickupId);
        _pickupRespawnTickById.Remove(pickupId);
    }

    public bool RespawnServerPeerAtSpawn(int peerId)
    {
        if (!_serverPlayers.TryGetValue(peerId, out ServerPlayer? player))
        {
            return false;
        }

        ServerClearEquippedItem(peerId);
        PlayerCharacter character = player.Character;
        character.GlobalPosition = SpawnPointForPeer(peerId);
        character.Velocity = Vector3.Zero;
        character.SetLook(SpawnYawForPeer(), 0.0f);
        character.ResetLocomotionFromAuthoritative(grounded: false);
        character.ResetInterpolationAfterSnap();
        return true;
    }

    public bool ServerTryEquipItem(int peerId, ItemId item, byte charges)
    {
        if (!IsServer)
        {
            return false;
        }

        if (!_serverPlayers.TryGetValue(peerId, out ServerPlayer? player))
        {
            return false;
        }

        if (player.EquippedItem != ItemId.None)
        {
            return false;
        }

        player.EquippedItem = item;
        player.EquippedCharges = charges;
        player.EquippedCooldownEndTick = 0;
        BroadcastInventoryStateForPeer(peerId);
        return true;
    }

    public void ServerClearEquippedItem(int peerId)
    {
        if (!_serverPlayers.TryGetValue(peerId, out ServerPlayer? player))
        {
            return;
        }

        player.EquippedItem = ItemId.None;
        player.EquippedCharges = 0;
        player.EquippedCooldownEndTick = 0;
        if (IsServer)
        {
            BroadcastInventoryStateForPeer(peerId);
        }
    }

    public void ServerClearAllEquippedItems()
    {
        foreach (KeyValuePair<int, ServerPlayer> pair in _serverPlayers)
        {
            int peerId = pair.Key;
            ServerPlayer player = pair.Value;
            player.EquippedItem = ItemId.None;
            player.EquippedCharges = 0;
            player.EquippedCooldownEndTick = 0;
            if (IsServer)
            {
                BroadcastInventoryStateForPeer(peerId);
            }
        }
    }

    public bool ServerTryConsumePickup(int peerId, int pickupId)
    {
        if (!IsServer)
        {
            GD.Print($"PickupConsumeRejected: peer={peerId} pickup={pickupId} reason=not_server");
            return false;
        }

        if (ServerCanPickupItem is not null && !ServerCanPickupItem(peerId))
        {
            GD.Print($"PickupConsumeRejected: peer={peerId} pickup={pickupId} reason=mode_gate");
            return false;
        }

        if (!_pickups.TryGetValue(pickupId, out PickupItem? pickup))
        {
            TryRecoverPickupRegistryEntry(pickupId, out pickup);
            if (pickup is null)
            {
                GD.Print($"PickupConsumeRejected: peer={peerId} pickup={pickupId} reason=pickup_not_registered");
                return false;
            }
        }

        if (_inactivePickups.Contains(pickupId))
        {
            GD.Print($"PickupConsumeRejected: peer={peerId} pickup={pickupId} reason=already_inactive");
            return false;
        }

        if (!ServerTryEquipItem(peerId, pickup.ItemId, pickup.Charges))
        {
            GD.Print($"PickupConsumeRejected: peer={peerId} pickup={pickupId} reason=equip_denied");
            return false;
        }

        _inactivePickups.Add(pickupId);
        int tickRate = Mathf.Max(1, TickRate);
        uint respawnDelayTicks = (uint)(30 * tickRate);
        _pickupRespawnTickById[pickupId] = _server_sim_tick + respawnDelayTicks;
        pickup.SetActive(false);
        BroadcastPickupState(pickupId, active: false);
        GD.Print($"PickupConsumed: peer={peerId} pickup={pickupId} item={pickup.ItemId} charges={pickup.Charges}");
        return true;
    }

    private bool TryRecoverPickupRegistryEntry(int pickupId, out PickupItem? pickup)
    {
        pickup = null;
        SceneTree? tree = GetTree();
        if (tree is null)
        {
            return false;
        }

        foreach (Node node in tree.GetNodesInGroup("pickup_items"))
        {
            if (node is not PickupItem candidate || candidate.PickupId != pickupId)
            {
                continue;
            }

            RegisterPickup(candidate);
            pickup = candidate;
            return true;
        }

        return false;
    }

    public void ServerResetAllPickups()
    {
        if (!IsServer)
        {
            return;
        }

        _inactivePickups.Clear();
        _pickupRespawnTickById.Clear();
        foreach (KeyValuePair<int, PickupItem> pair in _pickups)
        {
            int pickupId = pair.Key;
            PickupItem pickup = pair.Value;
            pickup.SetActive(true);
            BroadcastPickupState(pickupId, active: true);
        }
    }

    public bool IsFrozen(int peerId, uint tick)
    {
        return _freezeUntilTickByPeer.TryGetValue(peerId, out uint freezeUntilTick) && tick < freezeUntilTick;
    }

    public void ServerApplyFreeze(int targetPeerId, float durationSec)
    {
        if (!IsServer)
        {
            return;
        }

        if (!_serverPlayers.TryGetValue(targetPeerId, out ServerPlayer? targetPlayer))
        {
            return;
        }

        int durationTicks = Mathf.CeilToInt(Mathf.Max(0.0f, durationSec) * Mathf.Max(1, TickRate));
        uint frozenUntilTick = _server_sim_tick + (uint)Mathf.Max(0, durationTicks);
        _freezeUntilTickByPeer[targetPeerId] = frozenUntilTick;

        PlayerCharacter targetChar = targetPlayer.Character;
        targetChar.Velocity = Vector3.Zero;
        targetChar.ResetLocomotionFromAuthoritative(targetChar.Grounded);
        BroadcastFreezeStateForPeer(targetPeerId, frozenUntilTick);

        if (targetPeerId == _localPeerId)
        {
            uint nowTick = IsServer ? _server_sim_tick : GetEstimatedServerTickNow();
            bool frozenNow = IsFrozen(targetPeerId, nowTick);
            if (frozenNow && !_localFreezeActive)
            {
                _frozenYaw = _lookYaw;
                _frozenPitch = _lookPitch;
                _localFreezeActive = true;
            }
        }
    }

    public void SetDebugLogging(bool logControlPackets)
    {
        _logControlPackets = logControlPackets;
        PlayerMotor.LogFloorSnapDiagnostics = logControlPackets;
    }
    public void ApplyLocalViewSettings(float mouseSensitivity, bool invertLookY, float localFov)
    {
        _config.MouseSensitivity = Mathf.Clamp(mouseSensitivity, 0.0005f, 0.02f);
        _config.InvertLookY = invertLookY;
        _config.LocalFov = Mathf.Clamp(localFov, 60.0f, 120.0f);
        if (_localCharacter?.LocalCamera is Camera3D camera)
        {
            camera.Fov = _config.LocalFov;
        }
    }
    public void Initialize(NetworkConfig config, Node3D playerRoot)
    {
        _config = config;
        ProcessMode = ShouldKeepNetworkingWhenUnfocused() ? ProcessModeEnum.Always : ProcessModeEnum.Inherit;
        if (!ShouldKeepNetworkingWhenUnfocused())
        {
            RestoreUnfocusedNetworkingPolicyOverrides();
        }

        _playerRoot = playerRoot;
        _playerCharacterScene = GD.Load<PackedScene>(PlayerCharacterScenePath);
        _snapshotEveryTicks = Mathf.Max(1, _config.ServerTickRate / Mathf.Max(1, _config.SnapshotRate));
        _simLatency = _config.SimulatedLatencyMs;
        _simJitter = _config.SimulatedJitterMs;
        _simLoss = _config.SimulatedLossPercent;
        _simSeed = _config.SimulationSeed;
        _netClock = new NetClock(_config.ServerTickRate);
        TrySpawnLocalCharacter();
    }

    public void ApplySimulationOverride(bool enabled, int latency, int jitter, float loss, int seed)
    {
        _simEnabled = enabled;
        _simLatency = latency;
        _simJitter = jitter;
        _simLoss = loss;
        _simSeed = seed;
        _simulator?.Configure(_simEnabled, _simLatency, _simJitter, _simLoss);
    }

    public void SetSpawnOrigin(Transform3D spawnOrigin)
    {
        _spawnOrigin = spawnOrigin;
        _hasSpawnOrigin = true;
        _spawnYaw = _spawnOrigin.Basis.GetEuler().Y;
    }

    public void ClearSpawnOrigin()
    {
        _hasSpawnOrigin = false;
        _spawnOrigin = Transform3D.Identity;
        _spawnYaw = 0.0f;
    }
    public override void _Ready()
    {
        AddToGroup("net_session");
        ProcessMode = ShouldKeepNetworkingWhenUnfocused() ? ProcessModeEnum.Always : ProcessModeEnum.Inherit;
        // Godot polls MultiplayerAPI during process_frame by default; we poll manually in physics for fixed-step netcode.
        GetTree().SetMultiplayerPollEnabled(false);
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += OnConnectedToServer;
        Multiplayer.ConnectionFailed += OnConnectionFailed;
        Multiplayer.ServerDisconnected += OnServerDisconnected;
        _sceneMultiplayer = Multiplayer as SceneMultiplayer;
        if (_sceneMultiplayer is not null)
        {
            _sceneMultiplayer.Connect("peer_packet", Callable.From<long, byte[]>(OnPeerPacket));
        }
        else
        {
            GD.PushError("NetSession requires SceneMultiplayer for custom bytes transport.");
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        long simNetStartStamp = Stopwatch.GetTimestamp();
        int gc0Before = GC.CollectionCount(0);
        int gc1Before = GC.CollectionCount(1);
        int gc2Before = GC.CollectionCount(2);
        long memBeforeBytes = GC.GetTotalMemory(false);

        ApplyUnfocusedNetworkingPolicyIfNeeded();
        LogFrameHitchIfNeeded("physics", delta, _config.PhysicsHitchThresholdMs);
        if (Multiplayer.MultiplayerPeer is not null)
        {
            Multiplayer.Poll();
            _lastNetPollAtSec = Time.GetTicksMsec() / 1000.0;
        }
        if (_mode == RunMode.None)
        {
            return;
        }

        if (IsClient)
        {
            // Drive client prediction/input generation on a fixed client-tick cadence, independent of render pacing.
            float clientStepSec = 1.0f / Mathf.Max(1, _config.ClientTickRate);
            _clientTickAccumulatorSec += delta;
            if (_clientTickAccumulatorSec > 0.5)
            {
                _clientTickAccumulatorSec = 0.5;
            }

            int maxCatchUpSteps = Mathf.Max(1, Mathf.CeilToInt(0.5f / clientStepSec));
            int steps = 0;
            while (_clientTickAccumulatorSec >= clientStepSec && steps < maxCatchUpSteps)
            {
                CaptureInputState();
                TickClient(clientStepSec);
                _clientTickAccumulatorSec -= clientStepSec;
                steps++;
            }
        }

        if (IsServer)
        {
            TickServer((float)delta);
        }

        _simulator?.Flush(Time.GetTicksMsec() / 1000.0);
        UpdateMetrics();

        long simNetEndStamp = Stopwatch.GetTimestamp();
        float simNetDtMs = (float)((simNetEndStamp - simNetStartStamp) * 1000.0 / Stopwatch.Frequency);
        int gc0Delta = GC.CollectionCount(0) - gc0Before;
        int gc1Delta = GC.CollectionCount(1) - gc1Before;
        int gc2Delta = GC.CollectionCount(2) - gc2Before;
        long memDeltaKb = (GC.GetTotalMemory(false) - memBeforeBytes) / 1024;

        if (simNetDtMs > SimNetHitchThresholdMs || _tagProcessedDiagThisFrame)
        {
            GD.Print(
                $"SimNetDiag: dt_ms={simNetDtMs:0.0} gc_delta_counts={gc0Delta}/{gc1Delta}/{gc2Delta} " +
                $"mem_delta_kb={memDeltaKb} tag_processed={(_tagProcessedDiagThisFrame ? 1 : 0)} " +
                $"tag_tick={_tagProcessedDiagTick} tag_count={_tagProcessedDiagCountThisFrame}");
        }

        _tagProcessedDiagThisFrame = false;
        _tagProcessedDiagTick = 0;
        _tagProcessedDiagCountThisFrame = 0;
    }

    public override void _Process(double delta)
    {
        LogFrameHitchIfNeeded("process", delta, _config.ProcessHitchThresholdMs);
        if (_mode == RunMode.None)
        {
            return;
        }

        UpdateRemoteInterpolation();
        UpdateDebugDraws(Time.GetTicksMsec() / 1000.0);
        UpdateMetrics();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("debug_focus_harness_toggle"))
        {
            ToggleDebugFocusHarness();
            return;
        }

        if (_localCharacter is null || IsFocusInputSuppressed())
        {
            return;
        }

        if (@event.IsActionPressed("jump"))
        {
            TryLatchGroundedJump();
        }
        if (@event.IsActionPressed("fire"))
        {
            TryLatchFirePressed();
        }
        if (@event.IsActionPressed("interact"))
        {
            TryLatchInteractPressed();
        }

        if (@event.IsActionPressed("quit"))
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        if (@event is InputEventMouseButton mouseButton && mouseButton.Pressed && mouseButton.ButtonIndex == MouseButton.Left)
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (_localCharacter is null || IsFocusInputSuppressed() || IsLocalFrozenAtTick(_mode == RunMode.ListenServer ? _server_sim_tick : GetEstimatedServerTickNow()))
        {
            return;
        }

        if (@event is not InputEventMouseMotion mouseMotion || Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            return;
        }

        float maxPitch = Mathf.DegToRad(_config.PitchClampDegrees);
        float lookX = (float)(mouseMotion.Relative.X * _config.MouseSensitivity);
        float lookY = (float)(mouseMotion.Relative.Y * _config.MouseSensitivity);
        _lookYaw -= lookX;
        _lookPitch += _config.InvertLookY ? lookY : -lookY;
        _lookPitch = Mathf.Clamp(_lookPitch, -maxPitch, maxPitch);
        _localCharacter.SetLook(_lookYaw, _lookPitch);
    }

    private bool IsFocusInputSuppressed()
    {
        return !_hasFocus && !_config.AllowInputWhenUnfocused;
    }

    private bool ShouldKeepNetworkingWhenUnfocused()
    {
        return _config.KeepNetworkingWhenUnfocused;
    }

    private void ApplyUnfocusedNetworkingPolicyIfNeeded()
    {
        if (!ShouldKeepNetworkingWhenUnfocused() || _mode != RunMode.Client || !IsTransportConnected())
        {
            RestoreUnfocusedNetworkingPolicyOverrides();
            return;
        }

        if (OS.LowProcessorUsageMode)
        {
            OS.LowProcessorUsageMode = false;
        }

        if (_hasFocus)
        {
            RestoreUnfocusedNetworkingPolicyOverrides();
            return;
        }

        int minUnfocusedFps = Mathf.Max(1, _config.ClientTickRate);
        int currentMaxFps = Engine.MaxFps;
        if (currentMaxFps > 0 && currentMaxFps < minUnfocusedFps)
        {
            if (_savedMaxFpsBeforeUnfocus < 0)
            {
                _savedMaxFpsBeforeUnfocus = currentMaxFps;
            }

            Engine.MaxFps = minUnfocusedFps;
        }
    }

    private void RestoreUnfocusedNetworkingPolicyOverrides()
    {
        if (_savedMaxFpsBeforeUnfocus > 0 && Engine.MaxFps != _savedMaxFpsBeforeUnfocus)
        {
            Engine.MaxFps = _savedMaxFpsBeforeUnfocus;
        }

        _savedMaxFpsBeforeUnfocus = -1;
    }

    private void LogFrameHitchIfNeeded(string phase, double deltaSec, float thresholdMs)
    {
        if (!_config.EnableFrameHitchDiagnostics)
        {
            return;
        }

        float threshold = Mathf.Max(1.0f, thresholdMs);
        float deltaMs = (float)(deltaSec * 1000.0);
        if (deltaMs < threshold)
        {
            return;
        }

        double nowSec = Time.GetTicksMsec() / 1000.0;
        if (nowSec < _nextFrameHitchLogAtSec)
        {
            return;
        }

        _nextFrameHitchLogAtSec = nowSec + 0.2;
        uint estTick = _mode == RunMode.Client ? GetEstimatedServerTickNow() : _server_sim_tick;
        GD.Print(
            $"FrameHitchDiag: phase={phase} dt_ms={deltaMs:0.0} threshold_ms={threshold:0.0} " +
            $"mode={_mode} hasFocus={_hasFocus} allowUnfocused={_config.AllowInputWhenUnfocused} " +
            $"serverTick={_server_sim_tick} estTick={estTick}");
    }

    public bool StartListenServer(int port)
    {
        StopSession();

        ENetMultiplayerPeer peer = new();
        Error err = peer.CreateServer(port, _config.MaxPlayers, NetChannels.Count);
        if (err != Error.Ok)
        {
            GD.PushError($"CreateServer failed: {err}");
            return false;
        }
        Multiplayer.MultiplayerPeer = peer;
        _mode = RunMode.ListenServer;
        _localPeerId = Multiplayer.GetUniqueId();
        _simulator = new NetworkSimulator(_simSeed, SendPacketNow);
        _simulator.Configure(_simEnabled, _simLatency, _simJitter, _simLoss);

        _localCharacter = CreateCharacter(_localPeerId, true);
        PlayerCharacter localServerCharacter = CreateCharacter(_localPeerId, false, false);
        EnsureServerPlayer(_localPeerId, localServerCharacter);
        _lookYaw = _localCharacter.Yaw;
        _lookPitch = _localCharacter.Pitch;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        return true;
    }

    public bool StartDedicatedServer(int port)
    {
        StopSession();

        ENetMultiplayerPeer peer = new();
        Error err = peer.CreateServer(port, _config.MaxPlayers, NetChannels.Count);
        if (err != Error.Ok)
        {
            GD.PushError($"CreateServer failed: {err}");
            return false;
        }

        Multiplayer.MultiplayerPeer = peer;
        _mode = RunMode.DedicatedServer;
        _localPeerId = Multiplayer.GetUniqueId();
        _simulator = new NetworkSimulator(_simSeed, SendPacketNow);
        _simulator.Configure(_simEnabled, _simLatency, _simJitter, _simLoss);
        Input.MouseMode = Input.MouseModeEnum.Visible;
        return true;
    }

    public bool StartClient(string ip, int port)
    {
        StopSession();
        _playerRoot = null;

        ENetMultiplayerPeer peer = new();
        Error err = peer.CreateClient(ip, port, NetChannels.Count);
        if (err != Error.Ok)
        {
            GD.PushError($"CreateClient failed: {err}");
            return false;
        }
        Multiplayer.MultiplayerPeer = peer;
        _mode = RunMode.Client;
        _simulator = new NetworkSimulator(_simSeed, SendPacketNow);
        _simulator.Configure(_simEnabled, _simLatency, _simJitter, _simLoss);
        return true;
    }

    private void TrySpawnLocalCharacter()
    {
        if (_mode != RunMode.Client || !_welcomeReceived || _playerRoot is null || _localCharacter is not null || _localPeerId == 0)
        {
            return;
        }

        _localCharacter = CreateCharacter(_localPeerId, true);
        _lookYaw = _localCharacter.Yaw;
        _lookPitch = _localCharacter.Pitch;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        GD.Print($"NetSession: LocalCharacter spawned for peer {_localPeerId}");
    }

    public void StopSession()
    {
        RestoreUnfocusedNetworkingPolicyOverrides();
        _mode = RunMode.None;
        _clientTickAccumulatorSec = 0.0;
        _server_sim_tick = 0;
        _lastAuthoritativeServerTick = 0;
        _client_est_server_tick = 0;
        _inputEpoch = 1;
        _nextInputSeq = 0;
        _client_send_tick = 0;
        _lastAckedSeq = 0;
        _appliedInputDelayTicks = 0;
        _targetInputDelayTicks = 0;
        _delayTicksNextApplyAtSec = 0.0;
        _joinDelayGraceUntilSec = 0.0;
        _joinInitialInputDelayTicks = 0;
        _clientWelcomeTimeSec = 0.0;
        _clientJoinDiagUntilSec = 0.0;
        _clientNextJoinDiagAtSec = 0.0;
        _clientInputCmdsSentSinceLastDiag = 0;
        _lastCorrectionMeters = 0.0f;
        _lastCorrectionXZMeters = 0.0f;
        _lastCorrectionYMeters = 0.0f;
        _lastCorrection3DMeters = 0.0f;
        _correctionsPerSec = 0.0f;
        _correctionRateWindowStartSec = 0.0;
        _correctionRateWindowCount = 0;
        _rttMs = 0.0f;
        _jitterMs = 0.0f;
        _clientRttOutlierStreak = 0;
        _dynamicInterpolationDelayMs = 0.0f;
        _globalInterpDelayTicks = 0;
        _interpUnderflowExtraTicks = 0;
        _nextInterpDelayStepAtSec = 0.0;
        _nextInterpUnderflowAdjustAtSec = 0.0;
        _sessionSnapshotJitterEwmaMs = 0.0f;
        _lastSnapshotArrivalTimeSec = 0.0;
        _hasSnapshotArrivalTimeSec = false;
        _lastAuthoritativeSnapshotAtSec = 0.0;
        _lastServerTickObsAtSec = 0.0;
        _nextHardResyncAllowedAtSec = 0.0;
        _tickDriftGuardBreachCount = 0;
        _snapshotAgeMs = -1.0f;
        _pingSeq = 0;
        _nextPingTimeSec = 0.0;
        _nextServerDiagnosticsLogAtSec = 0.0;
        _jumpPressRepeatTicksRemaining = 0;
        _inputState = default;
        _pendingInputs = new InputHistoryBuffer();
        _serverDroppedOldInputCount = 0;
        _serverDroppedFutureInputCount = 0;
        _serverTicksUsedBufferedInput = 0;
        _serverTicksUsedHoldLast = 0;
        _serverTicksUsedNeutral = 0;
        _serverMissingInputStreakCurrent = 0;
        _serverMissingInputStreakMax = 0;
        _serverEffectiveDelayTicks = -1;
        _serverPeerRttMs = -1.0f;
        _serverPeerJitterMs = -1.0f;
        _tickErrorTicks = 0;
        _dropFutureRatePerSec = 0.0f;
        _dropFutureRateWindowStartSec = 0.0;
        _dropFutureRateWindowCount = 0;
        _resyncTriggered = false;
        _resyncCount = 0;
        _resyncSuppressedDuringJoinCount = 0;
        _nextResyncDiagLogAtSec = 0.0;
        _localHealth = MaxPlayerHealth;
        _localHealthMax = MaxPlayerHealth;
        ClearProjectileVisuals();
        ClearHitIndicator();
        foreach ((Node3D node, _) in _debugDrawNodes)
        {
            if (GodotObject.IsInstanceValid(node))
            {
                node.QueueFree();
            }
        }
        _debugDrawNodes.Clear();
        System.Array.Clear(_rewindTicks);
        System.Array.Clear(_rewindCounts);
        foreach (ServerPlayer player in _serverPlayers.Values)
        {
            player.Character.QueueFree();
        }

        foreach (RemoteEntity remote in _remotePlayers.Values)
        {
            remote.Character.QueueFree();
        }
        _serverPlayers.Clear();
        _serverCharactersByPeer.Clear();
        _remotePlayers.Clear();
        _clientInventory.Clear();
        _pickups.Clear();
        _inactivePickups.Clear();
        _pickupRespawnTickById.Clear();
        _freezeUntilTickByPeer.Clear();
        _clientTagDroneStatesByRunner.Clear();
        _pendingTagStateEventsByTick.Clear();
        _lastAppliedTagEventTick = 0;
        _localFreezeActive = false;
        _localCharacter?.QueueFree();
        _localCharacter = null;
        _localPeerId = 0;
        _welcomeReceived = false;
        CurrentMatchState = new MatchState
        {
            RoundIndex = 0,
            Phase = MatchPhase.Running,
            PhaseEndTick = 0
        };
        CurrentTagState = new TagState
        {
            RoundIndex = 0,
            ItPeerId = -1,
            ItCooldownEndTick = 0,
            TagAppliedTick = 0,
            TaggerPeerId = -1,
            TaggedPeerId = -1
        };
        _simulator = null;
        _pingSent.Clear();
        _hasFocus = true;
        _focusOutPending = false;
        _focusOutResetApplied = false;
        _focusOutStartedAtSec = 0.0;
        _debugFocusHarnessActive = false;
        _debugFocusHarnessUntilSec = 0.0;
        _clientFocusMode = ClientFocusMode.Focused;
        _lastNetPollAtSec = 0.0;
        _lastPacketProcessedAtSec = 0.0;
        _lastInputSendAtSec = 0.0;
        _lastSnapshotAppliedAtSec = 0.0;
        _hasLastLocalAuthoritativeSnapshot = false;
        _lastLocalAuthoritativeSnapshot = default;
        _lastLocalAuthoritativeServerTick = 0;
        _realtimeStallWindowStartSec = 0.0;
        _realtimeStallWindowMaxMs = 0.0f;
        _realtimeStallMs = 0.0f;
        _hardResetCount = 0;
        _lastHardResetReason = "none";
        _debugLastLoggedResyncCount = 0;
        _debugMissingWindowStartSec = 0.0;
        _debugMissingWindowStartMax = 0;
        _debugMissingWindowStartDroppedOld = 0;
        _debugMissingWindowStartDroppedFuture = 0;
        _tagProcessedDiagThisFrame = false;
        _tagProcessedDiagTick = 0;
        _tagProcessedDiagCountThisFrame = 0;

        if (Multiplayer.MultiplayerPeer is not null)
        {
            Multiplayer.MultiplayerPeer.Close();
            Multiplayer.MultiplayerPeer = null;
        }
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private bool IsLocalFrozenAtTick(uint tick)
    {
        if (!IsClient || _localPeerId <= 0)
        {
            _localFreezeActive = false;
            return false;
        }

        bool frozen = IsFrozen(_localPeerId, tick);
        if (frozen && !_localFreezeActive)
        {
            _frozenYaw = _lookYaw;
            _frozenPitch = _lookPitch;
            _localFreezeActive = true;
        }
        else if (!frozen)
        {
            _localFreezeActive = false;
        }

        return frozen;
    }

    private ItemId GetLocalEquippedItemForClientView()
    {
        if (!TryGetLocalInventoryState(out ItemId itemId, out _, out _))
        {
            return ItemId.None;
        }

        return itemId;
    }

    public bool TryGetLocalInventoryState(out ItemId itemId, out byte charges, out uint cooldownEndTick)
    {
        itemId = ItemId.None;
        charges = 0;
        cooldownEndTick = 0;
        if (_localPeerId <= 0)
        {
            return false;
        }

        if (IsServer && _serverPlayers.TryGetValue(_localPeerId, out ServerPlayer? localServerPlayer))
        {
            itemId = localServerPlayer.EquippedItem;
            charges = localServerPlayer.EquippedCharges;
            cooldownEndTick = localServerPlayer.EquippedCooldownEndTick;
            return true;
        }

        if (_clientInventory.TryGetValue(_localPeerId, out (byte ItemId, byte Charges, uint CooldownEndTick) inventory))
        {
            itemId = (ItemId)inventory.ItemId;
            charges = inventory.Charges;
            cooldownEndTick = inventory.CooldownEndTick;
            return true;
        }

        return false;
    }

    public bool TryGetLocalFreezeRemainingSec(out float remainingSec)
    {
        remainingSec = 0.0f;
        if (_localPeerId <= 0 || !_freezeUntilTickByPeer.TryGetValue(_localPeerId, out uint freezeUntilTick))
        {
            return false;
        }

        uint nowTick = IsServer ? _server_sim_tick : GetEstimatedServerTickNow();
        if (freezeUntilTick <= nowTick)
        {
            return false;
        }

        uint remainingTicks = freezeUntilTick - nowTick;
        remainingSec = remainingTicks / (float)Mathf.Max(1, TickRate);
        return remainingSec > 0.0f;
    }

    public void ServerBroadcastTagDroneState(int runnerPeerId, uint serverTick, Vector3 position, Vector3 velocity, bool visible)
    {
        if (!IsServer || runnerPeerId <= 0)
        {
            return;
        }

        NetCodec.WriteTagDroneState(_tagDroneStatePacket, runnerPeerId, serverTick, position, velocity, visible);
        foreach (int peerId in _serverPlayers.Keys)
        {
            if (_mode == RunMode.ListenServer && peerId == _localPeerId)
            {
                continue;
            }

            SendPacket(peerId, NetChannels.Snapshot, MultiplayerPeer.TransferModeEnum.UnreliableOrdered, _tagDroneStatePacket);
        }
    }

    public bool TryGetClientTagDroneState(int runnerPeerId, out TagDroneState state)
    {
        return _clientTagDroneStatesByRunner.TryGetValue(runnerPeerId, out state);
    }

    public void ClearClientTagDroneStates()
    {
        _clientTagDroneStatesByRunner.Clear();
    }

    private void MarkTagProcessedForDiag(uint tagTick)
    {
        _tagProcessedDiagThisFrame = true;
        _tagProcessedDiagTick = tagTick;
        _tagProcessedDiagCountThisFrame++;
    }

    private void QueueClientTagStateEvent(in TagState state, bool isFull)
    {
        if (_mode != RunMode.Client)
        {
            return;
        }

        uint tagTick = state.TagAppliedTick;
        if (tagTick == 0)
        {
            tagTick = _lastAuthoritativeServerTick;
        }

        TagState queuedState = state;
        queuedState.TagAppliedTick = tagTick;
        _pendingTagStateEventsByTick[tagTick] = new PendingTagStateEvent
        {
            State = queuedState,
            IsFull = isFull
        };
    }

    private static bool TryPeekFirstPendingTagTick(SortedDictionary<uint, PendingTagStateEvent> eventsByTick, out uint tick)
    {
        foreach (uint key in eventsByTick.Keys)
        {
            tick = key;
            return true;
        }

        tick = 0;
        return false;
    }

    private void ApplyQueuedTagStateEventsUpToTick(uint simTick, bool resimReplayed, uint serverTickForDiag)
    {
        while (TryPeekFirstPendingTagTick(_pendingTagStateEventsByTick, out uint nextTick) && nextTick <= simTick)
        {
            PendingTagStateEvent pending = _pendingTagStateEventsByTick[nextTick];
            _pendingTagStateEventsByTick.Remove(nextTick);
            ApplyClientTagStateEvent(pending.State, pending.IsFull, simTick, resimReplayed, serverTickForDiag);
        }
    }

    private void ApplyClientTagStateEvent(in TagState state, bool isFull, uint localSimTick, bool resimReplayed, uint serverTickForDiag)
    {
        bool staleRound = state.RoundIndex < CurrentTagState.RoundIndex;
        bool staleTick = state.TagAppliedTick < _lastAppliedTagEventTick;
        if (staleRound || staleTick)
        {
            return;
        }

        CurrentTagState = state;
        _lastAppliedTagEventTick = state.TagAppliedTick;
        MarkTagProcessedForDiag(state.TagAppliedTick);
        if (isFull)
        {
            TagStateFullReceived?.Invoke(state);
        }
        else
        {
            TagStateDeltaReceived?.Invoke(state);
        }

        GD.Print(
            $"TagApplyDiag: server_tick={serverTickForDiag} tag_tick={state.TagAppliedTick} " +
            $"applied_at_local_tick={localSimTick} resim_replayed={(resimReplayed ? "true" : "false")}");
    }

    private bool IsLocalWeaponCoolingDownAtTick(uint tick)
    {
        if (!TryGetLocalInventoryState(out ItemId itemId, out _, out uint cooldownEndTick))
        {
            return false;
        }

        if (itemId == ItemId.None)
        {
            return false;
        }

        return tick < cooldownEndTick;
    }
}
