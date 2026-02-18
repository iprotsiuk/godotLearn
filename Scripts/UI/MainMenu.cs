using Godot;
using NetRunnerSlice.GameModes;
using NetRunnerSlice.UI.Menu;

namespace NetRunnerSlice.UI;

public partial class MainMenu : Control
{
	private const string MainScreenScenePath = "res://Scenes/UI/Menu/MainMenuMainScreen.tscn";
	private const string SettingsScreenScenePath = "res://Scenes/UI/Menu/MainMenuSettingsScreen.tscn";

	[Signal]
	public delegate void HostRequestedEventHandler(int port, int selectedModeId, int roundTimeSec);

	[Signal]
	public delegate void JoinRequestedEventHandler(string ip, int port);

	[Signal]
	public delegate void QuitRequestedEventHandler();

	[Signal]
	public delegate void SettingsAppliedEventHandler(float mouseSensitivity, bool invertLookY, float localFov, int fpsLock);

	private Control? _content;
	private MainMenuMainScreen? _mainScreen;
	private MainMenuSettingsScreen? _settingsScreen;
	private string _defaultIp = "127.0.0.1";
	private int _defaultPort = 7777;
	private string _status = "Ready";
	private MenuSettings _settings = new();

	public override void _Ready()
	{
		_content = GetNode<Control>("Root/Panel/Content");
		ShowMainScreen();
	}

	public void SetDefaults(string ip, int port)
	{
		_defaultIp = string.IsNullOrWhiteSpace(ip) ? "127.0.0.1" : ip.Trim();
		_defaultPort = Mathf.Clamp(port, 1, 65535);
		_mainScreen?.SetDefaults(_defaultIp, _defaultPort);
	}

	public void SetStatus(string status)
	{
		_status = status;
		_mainScreen?.SetStatus(_status);
	}

	public void SetSettings(MenuSettings settings)
	{
		_settings = settings.Clone();
		_settingsScreen?.SetSettings(_settings);
		_mainScreen?.SetHostSettings(_settings.SelectedMode, _settings.RoundTimeSec);
	}

	public void OpenMainScreen()
	{
		ShowMainScreen();
	}

	private void ShowMainScreen()
	{
		ClearContent();
		if (_content is null)
		{
			return;
		}

		PackedScene scene = GD.Load<PackedScene>(MainScreenScenePath);
		_mainScreen = scene.Instantiate<MainMenuMainScreen>();
		_mainScreen.Name = "MainScreen";
		_content.AddChild(_mainScreen);

		_mainScreen.SetDefaults(_defaultIp, _defaultPort);
		_mainScreen.SetStatus(_status);

		_mainScreen.SetHostSettings(_settings.SelectedMode, _settings.RoundTimeSec);
		_mainScreen.HostPressed += (port, selectedModeId, roundTimeSec) =>
		{
			_settings.SelectedMode = Enum.IsDefined(typeof(GameModeId), selectedModeId)
				? (GameModeId)selectedModeId
				: GameModeId.FreeRun;
			_settings.RoundTimeSec = Mathf.Clamp(roundTimeSec, 30, 900);
			EmitSignal(SignalName.HostRequested, port, selectedModeId, roundTimeSec);
		};
		_mainScreen.JoinPressed += (ip, port) => EmitSignal(SignalName.JoinRequested, ip, port);
		_mainScreen.SettingsPressed += ShowSettingsScreen;
		_mainScreen.QuitPressed += () => EmitSignal(SignalName.QuitRequested);
	}

	private void ShowSettingsScreen()
	{
		ClearContent();
		if (_content is null)
		{
			return;
		}

		PackedScene scene = GD.Load<PackedScene>(SettingsScreenScenePath);
		_settingsScreen = scene.Instantiate<MainMenuSettingsScreen>();
		_settingsScreen.Name = "SettingsScreen";
		_content.AddChild(_settingsScreen);
		_settingsScreen.SetSettings(_settings);

		_settingsScreen.ApplyPressed += OnSettingsApplyPressed;
		_settingsScreen.BackPressed += ShowMainScreen;
	}

	private void OnSettingsApplyPressed(float sensitivity, bool invertLookY, float localFov, int fpsLock)
	{
		_settings.MouseSensitivity = Mathf.Clamp(sensitivity, 0.0005f, 0.02f);
		_settings.InvertLookY = invertLookY;
		_settings.LocalFov = Mathf.Clamp(localFov, 60.0f, 120.0f);
		_settings.FpsLock = fpsLock <= 0 ? 0 : fpsLock;
		EmitSignal(SignalName.SettingsApplied, _settings.MouseSensitivity, _settings.InvertLookY, _settings.LocalFov, _settings.FpsLock);
	}

	private void ClearContent()
	{
		_mainScreen = null;
		_settingsScreen = null;

		if (_content is null)
		{
			return;
		}

		foreach (Node child in _content.GetChildren())
		{
			child.QueueFree();
		}
	}
}
