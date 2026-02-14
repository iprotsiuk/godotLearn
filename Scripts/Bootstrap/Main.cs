// Scripts/Bootstrap/Main.cs
using Godot;
using NetRunnerSlice.Debug;
using NetRunnerSlice.GameModes;
using NetRunnerSlice.Net;
using NetRunnerSlice.UI;

namespace NetRunnerSlice.Bootstrap;

public partial class Main : Node
{
    private readonly IGameMode _gameMode = new FreeRunMode();

    private CliArgs? _cli;
    private NetworkConfig? _config;
    private NetSession? _session;
    private MainMenu? _menu;
    private DebugOverlay? _overlay;

    private Node3D? _playersRoot;
    private int _simSeed = 1337;

    public override void _Ready()
    {
        InputBootstrap.EnsureActions();
        _cli = CliArgs.Parse(OS.GetCmdlineArgs());
        _cli.ApplyWindow();

        _config = NetworkConfigLoader.Load("res://Config/network_config.json");

        BuildWorld();
        BuildUi();
        BuildSession();

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
                _menu?.Show();
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
        _gameMode.Exit();
    }

    private void BuildSession()
    {
        if (_config is null || _playersRoot is null)
        {
            return;
        }

        _session = new NetSession
        {
            Name = "NetSession"
        };
        AddChild(_session);
        _session.Initialize(_config, _playersRoot);

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
        _menu = new MainMenu
        {
            Name = "MainMenu"
        };
        AddChild(_menu);

        _overlay = new DebugOverlay
        {
            Name = "DebugOverlay"
        };
        AddChild(_overlay);

        _menu.HostRequested += StartHost;
        _menu.JoinRequested += StartJoin;
        _menu.QuitRequested += OnQuit;
        _overlay.NetSimChanged += OnNetSimChanged;

        if (_cli is not null)
        {
            _menu.SetDefaults(_cli.Ip, _cli.Port);
        }
    }

    private void BuildWorld()
    {
        Node3D world = new()
        {
            Name = "World"
        };
        AddChild(world);

        DirectionalLight3D light = new()
        {
            Rotation = new Vector3(-0.8f, -0.5f, 0.0f),
            LightEnergy = 2.2f,
            ShadowEnabled = true
        };
        world.AddChild(light);

        WorldEnvironment env = new();
        Godot.Environment environment = new()
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Aces,
            SsrEnabled = false,
            SsaoEnabled = true
        };
        env.Environment = environment;
        world.AddChild(env);

        CreateFloor(world);
        CreateBlock(world, new Vector3(6.0f, 0.5f, 0.0f), new Vector3(2.0f, 1.0f, 2.0f));
        CreateBlock(world, new Vector3(10.0f, 1.0f, -2.5f), new Vector3(2.0f, 2.0f, 2.0f));
        CreateBlock(world, new Vector3(14.0f, 1.5f, 0.5f), new Vector3(2.0f, 3.0f, 2.0f));

        _playersRoot = new Node3D
        {
            Name = "Players"
        };
        world.AddChild(_playersRoot);
    }

    private static void CreateFloor(Node3D parent)
    {
        StaticBody3D floorBody = new()
        {
            Name = "Floor"
        };

        CollisionShape3D shape = new();
        BoxShape3D box = new()
        {
            Size = new Vector3(120.0f, 1.0f, 120.0f)
        };
        shape.Shape = box;
        shape.Position = new Vector3(0.0f, -0.5f, 0.0f);
        floorBody.AddChild(shape);

        MeshInstance3D mesh = new()
        {
            Mesh = new BoxMesh
            {
                Size = box.Size
            },
            Position = shape.Position
        };

        StandardMaterial3D material = new()
        {
            AlbedoColor = new Color(0.23f, 0.25f, 0.27f),
            Roughness = 0.8f
        };
        mesh.MaterialOverride = material;

        floorBody.AddChild(mesh);
        parent.AddChild(floorBody);
    }

    private static void CreateBlock(Node3D parent, Vector3 pos, Vector3 size)
    {
        StaticBody3D body = new();
        CollisionShape3D shape = new()
        {
            Shape = new BoxShape3D { Size = size },
            Position = pos
        };

        MeshInstance3D mesh = new()
        {
            Mesh = new BoxMesh { Size = size },
            Position = pos
        };

        StandardMaterial3D material = new()
        {
            AlbedoColor = new Color(0.62f, 0.58f, 0.52f),
            Roughness = 0.9f
        };
        mesh.MaterialOverride = material;

        body.AddChild(shape);
        body.AddChild(mesh);
        parent.AddChild(body);
    }

    private void StartHost(int port)
    {
        if (_session is null)
        {
            return;
        }

        if (_session.StartListenServer(port))
        {
            _menu?.Hide();
        }
    }

    private void StartJoin(string ip, int port)
    {
        if (_session is null)
        {
            return;
        }

        if (_session.StartClient(ip, port))
        {
            _menu?.Hide();
        }
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
