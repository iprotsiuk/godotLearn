using Godot;
using NetRunnerSlice.GameModes;

namespace NetRunnerSlice.UI.Menu;

public partial class MainMenuMainScreen : VBoxContainer
{
	[Signal]
	public delegate void HostPressedEventHandler(int port, int selectedModeId, int roundTimeSec);

	[Signal]
	public delegate void JoinPressedEventHandler(string ip, int port);

	[Signal]
	public delegate void SettingsPressedEventHandler();

	[Signal]
	public delegate void QuitPressedEventHandler();

	private LineEdit? _ipEdit;
	private LineEdit? _portEdit;
	private OptionButton? _modeOption;
	private SpinBox? _roundTimeSpin;
	private Label? _statusLabel;

	public override void _Ready()
	{
		_ipEdit = GetNode<LineEdit>("IpEdit");
		_portEdit = GetNode<LineEdit>("PortEdit");
		_modeOption = GetNode<OptionButton>("ModeOption");
		_roundTimeSpin = GetNode<SpinBox>("RoundTimeSecSpin");
		_statusLabel = GetNode<Label>("StatusLabel");

		GetNode<Button>("HostButton").Pressed += OnHostPressed;
		GetNode<Button>("JoinButton").Pressed += OnJoinPressed;
		GetNode<Button>("SettingsButton").Pressed += OnSettingsPressed;
		GetNode<Button>("QuitButton").Pressed += OnQuitPressed;

		PopulateModeOptions();
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

	public void SetHostSettings(GameModeId selectedMode, int roundTimeSec)
	{
		SetSelectedMode(selectedMode);
		if (_roundTimeSpin is not null)
		{
			_roundTimeSpin.Value = ClampRoundTime(roundTimeSec);
		}
	}

	private void OnHostPressed()
	{
		GameModeId selectedMode = GetSelectedMode();
		int roundTimeSec = GetRoundTimeSec();
		EmitSignal(SignalName.HostPressed, ParsePort(), (int)selectedMode, roundTimeSec);
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

	private void PopulateModeOptions()
	{
		if (_modeOption is null)
		{
			return;
		}

		_modeOption.Clear();
		_modeOption.AddItem("FreeRun", (int)GameModeId.FreeRun);
		_modeOption.AddItem("TagClassic", (int)GameModeId.TagClassic);
	}

	private void SetSelectedMode(GameModeId selectedMode)
	{
		if (_modeOption is null)
		{
			return;
		}

		int targetId = Enum.IsDefined(typeof(GameModeId), selectedMode)
			? (int)selectedMode
			: (int)GameModeId.FreeRun;

		for (int i = 0; i < _modeOption.ItemCount; i++)
		{
			if (_modeOption.GetItemId(i) == targetId)
			{
				_modeOption.Selected = i;
				return;
			}
		}

		_modeOption.Selected = 0;
	}

	private GameModeId GetSelectedMode()
	{
		if (_modeOption is null || _modeOption.Selected < 0)
		{
			return GameModeId.FreeRun;
		}

		int raw = _modeOption.GetItemId(_modeOption.Selected);
		return Enum.IsDefined(typeof(GameModeId), raw) ? (GameModeId)raw : GameModeId.FreeRun;
	}

	private int GetRoundTimeSec()
	{
		if (_roundTimeSpin is null)
		{
			return 120;
		}

		return ClampRoundTime((int)_roundTimeSpin.Value);
	}

	private static int ClampRoundTime(int roundTimeSec)
	{
		int clamped = Mathf.Clamp(roundTimeSec, 30, 900);
		return (clamped / 10) * 10;
	}
}
