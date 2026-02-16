using Godot;

namespace NetRunnerSlice.UI.Menu;

public partial class MainMenuMainScreen : VBoxContainer
{
	[Signal]
	public delegate void HostPressedEventHandler(int port);

	[Signal]
	public delegate void JoinPressedEventHandler(string ip, int port);

	[Signal]
	public delegate void SettingsPressedEventHandler();

	[Signal]
	public delegate void QuitPressedEventHandler();

	private LineEdit? _ipEdit;
	private LineEdit? _portEdit;
	private Label? _statusLabel;

	public override void _Ready()
	{
		_ipEdit = GetNode<LineEdit>("IpEdit");
		_portEdit = GetNode<LineEdit>("PortEdit");
		_statusLabel = GetNode<Label>("StatusLabel");

		GetNode<Button>("HostButton").Pressed += OnHostPressed;
		GetNode<Button>("JoinButton").Pressed += OnJoinPressed;
		GetNode<Button>("SettingsButton").Pressed += OnSettingsPressed;
		GetNode<Button>("QuitButton").Pressed += OnQuitPressed;
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
		EmitSignal(SignalName.HostPressed, ParsePort());
	}

	private void OnJoinPressed()
	{
		int port = ParsePort();
		string ip = _ipEdit?.Text.Trim() ?? "127.0.0.1";
		if (string.IsNullOrEmpty(ip))
		{
			ip = "127.0.0.1";
		}

		EmitSignal(SignalName.JoinPressed, ip, port);
	}

	private void OnSettingsPressed()
	{
		EmitSignal(SignalName.SettingsPressed);
	}

	private void OnQuitPressed()
	{
		EmitSignal(SignalName.QuitPressed);
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
