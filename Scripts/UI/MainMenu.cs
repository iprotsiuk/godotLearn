// Scripts/UI/MainMenu.cs
using Godot;

namespace NetRunnerSlice.UI;

public partial class MainMenu : CanvasLayer
{
    [Signal]
    public delegate void HostRequestedEventHandler(int port);

    [Signal]
    public delegate void JoinRequestedEventHandler(string ip, int port);

    [Signal]
    public delegate void QuitRequestedEventHandler();

    private LineEdit? _ipEdit;
    private LineEdit? _portEdit;

    public override void _Ready()
    {
        Layer = 10;

        Control root = new()
        {
            Name = "Root",
            MouseFilter = Control.MouseFilterEnum.Stop,
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f
        };
        AddChild(root);

        Panel panel = new()
        {
            Size = new Vector2(420.0f, 280.0f),
            Position = new Vector2(40.0f, 40.0f)
        };
        root.AddChild(panel);

        VBoxContainer vbox = new()
        {
            Position = new Vector2(16.0f, 16.0f),
            Size = new Vector2(388.0f, 248.0f)
        };
        panel.AddChild(vbox);

        Label title = new()
        {
            Text = "NetRunner Slice",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        vbox.AddChild(title);

        Label ipLabel = new() { Text = "Server IP" };
        vbox.AddChild(ipLabel);

        _ipEdit = new LineEdit { Text = "127.0.0.1" };
        vbox.AddChild(_ipEdit);

        Label portLabel = new() { Text = "Port" };
        vbox.AddChild(portLabel);

        _portEdit = new LineEdit { Text = "7777" };
        vbox.AddChild(_portEdit);

        Button hostButton = new() { Text = "Host" };
        hostButton.Pressed += OnHostPressed;
        vbox.AddChild(hostButton);

        Button joinButton = new() { Text = "Join" };
        joinButton.Pressed += OnJoinPressed;
        vbox.AddChild(joinButton);

        Button quitButton = new() { Text = "Quit" };
        quitButton.Pressed += () => EmitSignal(SignalName.QuitRequested);
        vbox.AddChild(quitButton);

        Label hint = new()
        {
            Text = "Mouse: look | WASD: move | Space: jump | Esc: release mouse",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        vbox.AddChild(hint);
    }

    public void SetDefaults(string ip, int port)
    {
        if (_ipEdit is not null)
        {
            _ipEdit.Text = ip;
        }

        if (_portEdit is not null)
        {
            _portEdit.Text = port.ToString();
        }
    }

    private void OnHostPressed()
    {
        int port = ParsePort();
        EmitSignal(SignalName.HostRequested, port);
    }

    private void OnJoinPressed()
    {
        int port = ParsePort();
        string ip = _ipEdit?.Text.Trim() ?? "127.0.0.1";
        if (string.IsNullOrEmpty(ip))
        {
            ip = "127.0.0.1";
        }

        EmitSignal(SignalName.JoinRequested, ip, port);
    }

    private int ParsePort()
    {
        if (_portEdit is not null && int.TryParse(_portEdit.Text.Trim(), out int parsed))
        {
            return Mathf.Clamp(parsed, 1, 65535);
        }

        return 7777;
    }
}
