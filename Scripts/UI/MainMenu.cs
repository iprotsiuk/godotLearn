// Scripts/UI/MainMenu.cs
using Godot;

namespace NetRunnerSlice.UI;

public partial class MainMenu : Control
{
	[Signal]
	public delegate void HostRequestedEventHandler(int port);

	[Signal]
	public delegate void JoinRequestedEventHandler(string ip, int port);

	[Signal]
	public delegate void QuitRequestedEventHandler();

	private LineEdit? _ipEdit;
	private LineEdit? _portEdit;
	private Label? _statusLabel;

	public override void _Ready()
	{
		_ipEdit = GetNode<LineEdit>("Root/Panel/VBox/IpEdit");
		_portEdit = GetNode<LineEdit>("Root/Panel/VBox/PortEdit");
		_statusLabel = GetNode<Label>("Root/Panel/VBox/StatusLabel");

		GetNode<Button>("Root/Panel/VBox/HostButton").Pressed += OnHostPressed;
		GetNode<Button>("Root/Panel/VBox/JoinButton").Pressed += OnJoinPressed;
		GetNode<Button>("Root/Panel/VBox/QuitButton").Pressed += OnQuitPressed;
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

	public void SetStatus(string status)
	{
		if (_statusLabel is not null)
		{
			_statusLabel.Text = status;
		}
	}

	private void OnHostPressed()
	{
		int port = ParsePort();
		GD.Print($"MainMenu: Host pressed (port={port})");
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

		GD.Print($"MainMenu: Join pressed (ip={ip}, port={port})");
		EmitSignal(SignalName.JoinRequested, ip, port);
	}

	private void OnQuitPressed()
	{
		GD.Print("MainMenu: Quit pressed");
		EmitSignal(SignalName.QuitRequested);
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
