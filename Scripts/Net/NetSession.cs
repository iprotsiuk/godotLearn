// Scripts/Net/NetSession.cs
using System.Collections.Generic;
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

    private enum RunMode
    {
        None,
        ListenServer,
        DedicatedServer,
        Client
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
    private readonly List<int> _pickupRespawnReadyScratch = new();
    private readonly byte[] _inputPacket = new byte[NetConstants.InputPacketBytes];
    private readonly byte[] _snapshotPacket = new byte[NetConstants.SnapshotPacketBytes];
    private readonly byte[] _controlPacket = new byte[NetConstants.ControlPacketBytes];
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
    private bool _focusOutPending;
    private bool _focusOutResetApplied;
    private double _focusOutStartedAtSec;
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
        ItCooldownEndTick = 0
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
    public System.Func<int, bool>? ServerCanPickupItem;

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
        if (Multiplayer.MultiplayerPeer is not null)
        {
            Multiplayer.Poll();
        }
        if (_mode == RunMode.None)
        {
            return;
        }

        if (IsClient)
        {
            // Sample gameplay movement input in the same fixed-step loop used for prediction/simulation.
            CaptureInputState();
            TickClient((float)delta);
        }

        if (IsServer)
        {
            TickServer((float)delta);
        }

        _simulator?.Flush(Time.GetTicksMsec() / 1000.0);
        UpdateMetrics();
    }

    public override void _Process(double delta)
    {
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
        if (_localCharacter is null || !_hasFocus)
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
        if (_localCharacter is null || !_hasFocus || IsLocalFrozenAtTick(_mode == RunMode.ListenServer ? _server_sim_tick : GetEstimatedServerTickNow()))
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
        _mode = RunMode.None;
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
            ItCooldownEndTick = 0
        };
        _simulator = null;
        _pingSent.Clear();

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
