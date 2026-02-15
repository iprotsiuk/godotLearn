// Scripts/Bootstrap/Main.cs
using Godot;
using NetRunnerSlice.Debug;
using NetRunnerSlice.GameModes;
using NetRunnerSlice.Net;
using NetRunnerSlice.UI;
namespace NetRunnerSlice.Bootstrap;
public partial class Main : Node
{
    private const string MainMenuScenePath = "res://Scenes/UI/MainMenu.tscn";
    private const string TestWorldScenePath = "res://Scenes/testWorld.tscn";
    private readonly IGameMode _gameMode = new FreeRunMode();
    private CliArgs? _cli;
    private NetworkConfig? _config;
    private NetSession? _session;
    private MainMenu? _menu;
    private DebugOverlay? _overlay;
    private Node? _sceneRoot;
    private Node3D? _activeWorld;
    private Node3D? _playersRoot;
    private bool _hasWorldSpawnOrigin;
    private Transform3D _worldSpawnOrigin = Transform3D.Identity;
    private bool _joinPending;
    private string _pendingJoinIp = "127.0.0.1";
    private int _pendingJoinPort = 7777;
    private int _simSeed = 1337;
    private string _activeProfile = "DEFAULT";
    public override void _Ready()
    {
        InputBootstrap.EnsureActions();
        _cli = CliArgs.Parse(OS.GetCmdlineArgs());
        _cli.ApplyWindow();
        string configPath = _cli.Profile == NetworkProfile.Lan ? "res://Config/network_config_lan.json" :
            (_cli.Profile == NetworkProfile.Wan ? "res://Config/network_config_wan.json" : "res://Config/network_config.json");
        _activeProfile = _cli.Profile == NetworkProfile.Lan ? "LAN" : (_cli.Profile == NetworkProfile.Wan ? "WAN" : "DEFAULT");
        _config = NetworkConfigLoader.Load(configPath);
        BuildUi();
        EnsureSceneRoot();
        EnsureSession();
        HookMultiplayerSignals();
        _gameMode.Enter();
        switch (_cli.Role)
        {
            case StartupRole.Host:
                StartHost(_cli.Port);
                break;
            case StartupRole.Join:
                StartJoin(_cli.Ip, _cli.Port);
                break;
            default:
                ShowMenu("Ready");
                break;
        }
    }
    public override void _Process(double delta)
    {
        if (_session is null || _overlay is null)
        {
            return;
        }
        _overlay.Update(_session.Metrics, _session.IsServer, _session.IsClient);
    }
    public override void _ExitTree()
    {
        UnhookMultiplayerSignals();
        _session?.StopSession();
        _gameMode.Exit();
    }
    private void HookMultiplayerSignals()
    {
        Multiplayer.ConnectedToServer += OnConnectedToServer;
        Multiplayer.ConnectionFailed += OnConnectionFailed;
        Multiplayer.ServerDisconnected += OnServerDisconnected;
    }
    private void UnhookMultiplayerSignals()
    {
        Multiplayer.ConnectedToServer -= OnConnectedToServer;
        Multiplayer.ConnectionFailed -= OnConnectionFailed;
        Multiplayer.ServerDisconnected -= OnServerDisconnected;
    }
    private void EnsureSession()
    {
        if (_session is not null)
        {
            return;
        }
        _session = new NetSession
        {
            Name = "NetSession"
        };
        AddChild(_session);
        ApplySimFromCliAndConfig();
    }
    private void ApplySimFromCliAndConfig()
    {
        if (_session is null || _config is null)
        {
            return;
        }
        bool simEnabled = _cli is not null && _cli.SimulationEnabled;
        int simLatency = _cli?.SimLatencyMs ?? _config.SimulatedLatencyMs;
        int simJitter = _cli?.SimJitterMs ?? _config.SimulatedJitterMs;
        float simLoss = _cli?.SimLossPercent ?? _config.SimulatedLossPercent;
        _simSeed = _cli?.SimSeed ?? _config.SimulationSeed;
        _session.ApplySimulationOverride(simEnabled, simLatency, simJitter, simLoss, _simSeed);
        _overlay?.SetSimulationControls(simEnabled, simLatency, simJitter, simLoss);
    }
    private void BuildUi()
    {
        PackedScene menuScene = GD.Load<PackedScene>(MainMenuScenePath);
        _menu = menuScene.Instantiate<MainMenu>();
        _menu.Name = "MainMenu";
        AddChild(_menu);
        _overlay = new DebugOverlay { Name = "DebugOverlay" };
        AddChild(_overlay);
        _menu.HostRequested += StartHost;
        _menu.JoinRequested += StartJoin;
        _menu.QuitRequested += OnQuit;
        _overlay.NetSimChanged += OnNetSimChanged;
        _overlay.SetProfileName(_activeProfile);
        if (_cli is not null)
        {
            _menu.SetDefaults(_cli.Ip, _cli.Port);
        }
        _menu.SetStatus("Ready");
    }
    private void EnsureSceneRoot()
    {
        _sceneRoot = GetNodeOrNull<Node>("SceneRoot");
        if (_sceneRoot is not null)
        {
            return;
        }
        _sceneRoot = new Node
        {
            Name = "SceneRoot"
        };
        AddChild(_sceneRoot);
    }
    private bool LoadTestWorld()
    {
        if (_sceneRoot is null)
        {
            GD.PushError("SceneRoot is missing.");
            return false;
        }
        if (_activeWorld is not null)
        {
            return true;
        }
        PackedScene worldScene = GD.Load<PackedScene>(TestWorldScenePath);
        Node worldNode = worldScene.Instantiate();
        if (worldNode is not Node3D world3D)
        {
            GD.PushError($"World scene root is not Node3D: {TestWorldScenePath}");
            worldNode.QueueFree();
            return false;
        }
        _activeWorld = world3D;
        _sceneRoot.AddChild(_activeWorld);
        Node3D? protoMarker = _activeWorld.GetNodeOrNull<Node3D>("ProtoController");
        if (protoMarker is not null)
        {
            _worldSpawnOrigin = protoMarker.GlobalTransform;
            _hasWorldSpawnOrigin = true;
            protoMarker.QueueFree();
        }
        else
        {
            _worldSpawnOrigin = Transform3D.Identity;
            _hasWorldSpawnOrigin = false;
        }
        _playersRoot = new Node3D
        {
            Name = "Players"
        };
        _activeWorld.AddChild(_playersRoot);
        return true;
    }
    private void UnloadWorld()
    {
        _playersRoot = null;
        _hasWorldSpawnOrigin = false;
        _worldSpawnOrigin = Transform3D.Identity;
        if (_activeWorld is null)
        {
            return;
        }
        _activeWorld.QueueFree();
        _activeWorld = null;
    }
    private bool InitializeSessionForWorld()
    {
        if (_session is null || _config is null || _playersRoot is null)
        {
            return false;
        }
        if (_hasWorldSpawnOrigin)
        {
            _session.SetSpawnOrigin(_worldSpawnOrigin);
        }
        else
        {
            _session.ClearSpawnOrigin();
        }
        _session.Initialize(_config, _playersRoot);
        ApplySimFromCliAndConfig();
        return true;
    }
    private void StartHost(int port)
    {
        _joinPending = false;
        _session?.StopSession();
        UnloadWorld();
        ShowMenu("Hosting...");
        if (!LoadTestWorld() || !InitializeSessionForWorld() || _session is null)
        {
            ShowMenu("Failed to load test world.");
            return;
        }
        if (_session.StartListenServer(port))
        {
            _menu?.Hide();
            Input.MouseMode = Input.MouseModeEnum.Captured;
            return;
        }
        UnloadWorld();
        ShowMenu("Host start failed.");
    }
    private void StartJoin(string ip, int port)
    {
        _pendingJoinIp = ip;
        _pendingJoinPort = port;
        _joinPending = true;
        _session?.StopSession();
        UnloadWorld();
        ShowMenu("Connecting...");
        if (_session is null)
        {
            ShowMenu("Network session unavailable.");
            _joinPending = false;
            return;
        }
        if (!_session.StartClient(ip, port))
        {
            ShowMenu("Client start failed.");
            _joinPending = false;
        }
    }
    private void ShowMenu(string status)
    {
        _menu?.Show();
        _menu?.SetStatus(status);
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }
    private void OnConnectedToServer()
    {
        if (!_joinPending)
        {
            return;
        }
        if (!LoadTestWorld() || !InitializeSessionForWorld())
        {
            _session?.StopSession();
            _joinPending = false;
            ShowMenu("Connected, but world setup failed.");
            UnloadWorld();
            return;
        }
        _joinPending = false;
        _menu?.SetStatus($"Connected to {_pendingJoinIp}:{_pendingJoinPort}");
        _menu?.Hide();
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }
    private void OnConnectionFailed()
    {
        if (!_joinPending)
        {
            return;
        }
        _joinPending = false;
        UnloadWorld();
        ShowMenu("Connection failed.");
    }
    private void OnServerDisconnected()
    {
        UnloadWorld();
        ShowMenu("Disconnected from server.");
    }
    private void OnQuit()
    {
        GetTree().Quit();
    }
    private void OnNetSimChanged(bool enabled, int latencyMs, int jitterMs, float lossPercent)
    {
        _session?.ApplySimulationOverride(enabled, latencyMs, jitterMs, lossPercent, _simSeed);
    }
}
