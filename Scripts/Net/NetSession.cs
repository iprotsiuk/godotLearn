// Scripts/Net/NetSession.cs
using System.Collections.Generic;
using Godot;
using NetRunnerSlice.Player;

namespace NetRunnerSlice.Net;

public partial class NetSession : Node
{
    private const int RewindHistoryTicks = 120;
    private const double ServerDiagnosticsLogIntervalSec = 2.0;
    private const double JoinDiagnosticsLogIntervalSec = 0.25;
    private const double ClientResyncJoinGraceSec = 2.0;
    private const int ClientInputSafetyTicks = 2;
    private const float WeaponMaxRange = 200.0f;
    private const float WeaponTargetRadius = 0.5f;
    private const float WeaponOriginMaxOffset = 1.5f;

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
    private readonly Dictionary<int, RemoteEntity> _remotePlayers = new();
    private readonly byte[] _inputPacket = new byte[NetConstants.InputPacketBytes];
    private readonly byte[] _snapshotPacket = new byte[NetConstants.SnapshotPacketBytes];
    private readonly byte[] _controlPacket = new byte[NetConstants.ControlPacketBytes];
    private readonly InputCommand[] _inputDecodeScratch = new InputCommand[NetConstants.MaxInputRedundancy];
    private readonly InputCommand[] _inputSendScratch = new InputCommand[NetConstants.MaxInputRedundancy];
    private readonly PlayerStateSnapshot[] _snapshotDecodeScratch = new PlayerStateSnapshot[NetConstants.MaxPlayers];
    private readonly PlayerStateSnapshot[] _snapshotSendScratch = new PlayerStateSnapshot[NetConstants.MaxPlayers];
    private RunMode _mode;
    private NetworkConfig _config = new();
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
    private float _lastCorrectionMeters;
    private float _lastCorrectionXZMeters;
    private float _lastCorrectionYMeters;
    private float _lastCorrection3DMeters;
    private float _correctionsPerSec;
    private double _correctionRateWindowStartSec;
    private uint _correctionRateWindowCount;
    private float _rttMs;
    private float _jitterMs;
    private bool _logControlPackets;
    private float _dynamicInterpolationDelayMs;
    private int _globalInterpDelayTicks;
    private int _interpUnderflowExtraTicks;
    private double _nextInterpDelayStepAtSec;
    private double _nextInterpUnderflowAdjustAtSec;
    private float _sessionSnapshotJitterEwmaMs;
    private double _lastSnapshotArrivalTimeSec;
    private bool _hasSnapshotArrivalTimeSec;
    private ushort _pingSeq;
    private double _nextPingTimeSec;
    private readonly Dictionary<ushort, double> _pingSent = new();
    private double _nextServerDiagnosticsLogAtSec;
    private bool _hasFocus = true;
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
    private PlayerCharacter? _localCharacter;
    public bool IsServer => _mode == RunMode.ListenServer || _mode == RunMode.DedicatedServer;
    public bool IsClient => _mode == RunMode.ListenServer || _mode == RunMode.Client;
    public SessionMetrics Metrics { get; private set; }

    public void SetDebugLogging(bool logControlPackets)
    {
        _logControlPackets = logControlPackets;
    }
    public void Initialize(NetworkConfig config, Node3D playerRoot)
    {
        _config = config;
        _playerRoot = playerRoot;
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

        // Dedicated server runs authoritative simulation only and must not poll local gameplay input.
        if (IsClient)
        {
            CaptureInputState();
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
        if (@event is InputEventMouseMotion mouseMotion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            float maxPitch = Mathf.DegToRad(_config.PitchClampDegrees);
            _lookYaw -= (float)(mouseMotion.Relative.X * _config.MouseSensitivity);
            _lookPitch -= (float)(mouseMotion.Relative.Y * _config.MouseSensitivity);
            _lookPitch = Mathf.Clamp(_lookPitch, -maxPitch, maxPitch);
            _localCharacter.SetLook(_lookYaw, _lookPitch);
        }
        if (@event.IsActionPressed("jump"))
        {
            TryLatchGroundedJump();
        }
        if (@event.IsActionPressed("fire"))
        {
            TryLatchFirePressed();
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
        _dynamicInterpolationDelayMs = 0.0f;
        _globalInterpDelayTicks = 0;
        _interpUnderflowExtraTicks = 0;
        _nextInterpDelayStepAtSec = 0.0;
        _nextInterpUnderflowAdjustAtSec = 0.0;
        _sessionSnapshotJitterEwmaMs = 0.0f;
        _lastSnapshotArrivalTimeSec = 0.0;
        _hasSnapshotArrivalTimeSec = false;
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
        _remotePlayers.Clear();
        _localCharacter?.QueueFree();
        _localCharacter = null;
        _localPeerId = 0;
        _welcomeReceived = false;
        _simulator = null;
        _pingSent.Clear();

        if (Multiplayer.MultiplayerPeer is not null)
        {
            Multiplayer.MultiplayerPeer.Close();
            Multiplayer.MultiplayerPeer = null;
        }
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }
}
