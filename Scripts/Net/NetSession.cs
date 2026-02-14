// Scripts/Net/NetSession.cs
using System.Collections.Generic;
using Godot;
using NetRunnerSlice.Player;

namespace NetRunnerSlice.Net;

public partial class NetSession : Node
{
    private enum RunMode
    {
        None,
        ListenServer,
        Client
    }

    private sealed class ServerPlayer
    {
        public required PlayerCharacter Character;
        public ServerInputBuffer Inputs { get; } = new();
        public bool HasStartedInputStream;
        public uint LastProcessedSeq;
        public InputCommand LastInput;
    }

    private sealed class RemoteEntity
    {
        public required PlayerCharacter Character;
        public RemoteSnapshotBuffer Buffer { get; } = new();
    }

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

    private uint _serverTick;
    private uint _clientTick;
    private int _localPeerId;
    private int _snapshotEveryTicks = 3;

    private bool _simEnabled;
    private int _simLatency;
    private int _simJitter;
    private float _simLoss;
    private int _simSeed;

    private float _lookYaw;
    private float _lookPitch;
    private bool _jumpHeldLastTick;

    private float _lastCorrectionMeters;
    private float _rttMs;
    private float _jitterMs;
    private ushort _pingSeq;
    private double _nextPingTimeSec;

    private readonly Dictionary<ushort, double> _pingSent = new();

    private InputHistoryBuffer _pendingInputs = new();
    private uint _nextInputSeq;
    private uint _lastAckedSeq;

    private PlayerCharacter? _localCharacter;

    public bool IsServer => _mode == RunMode.ListenServer;

    public bool IsClient => _mode == RunMode.ListenServer || _mode == RunMode.Client;

    public SessionMetrics Metrics { get; private set; }

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

        UpdateRemoteInterpolation();
        UpdateMetrics();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_localCharacter is null)
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
        _serverTick = 0;
        _clientTick = 0;
        _nextInputSeq = 0;
        _lastAckedSeq = 0;
        _lastCorrectionMeters = 0.0f;
        _pendingInputs = new InputHistoryBuffer();

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
