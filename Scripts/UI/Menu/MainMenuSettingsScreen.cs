using Godot;

namespace NetRunnerSlice.UI.Menu;

public partial class MainMenuSettingsScreen : VBoxContainer
{
	[Signal]
	public delegate void ApplyPressedEventHandler(float mouseSensitivity, bool invertLookY, float localFov, int fpsLock);

	[Signal]
	public delegate void BackPressedEventHandler();

	private HSlider? _sensitivitySlider;
	private Label? _sensitivityValue;
	private CheckBox? _invertLookY;
	private HSlider? _fovSlider;
	private Label? _fovValue;
	private OptionButton? _fpsLockOption;
	private HSlider? _masterVolumeSlider;
	private Label? _masterVolumeValue;
	private TabContainer? _tabs;

	public override void _Ready()
	{
		_tabs = GetNode<TabContainer>("Tabs");
		_sensitivitySlider = GetNode<HSlider>("Tabs/ControlsTab/SensitivityRow/SensitivitySlider");
		_sensitivityValue = GetNode<Label>("Tabs/ControlsTab/SensitivityRow/SensitivityValue");
		_invertLookY = GetNode<CheckBox>("Tabs/ControlsTab/InvertLookY");
		_fovSlider = GetNode<HSlider>("Tabs/VideoTab/FovRow/FovSlider");
		_fovValue = GetNode<Label>("Tabs/VideoTab/FovRow/FovValue");
		_fpsLockOption = GetNode<OptionButton>("Tabs/VideoTab/FpsLockOption");
		_masterVolumeSlider = GetNode<HSlider>("Tabs/AudioTab/MasterVolumeRow/MasterVolumeSlider");
		_masterVolumeValue = GetNode<Label>("Tabs/AudioTab/MasterVolumeRow/MasterVolumeValue");

		GetNode<Button>("ButtonsRow/ApplyButton").Pressed += OnApplyPressed;
		GetNode<Button>("ButtonsRow/BackButton").Pressed += OnBackPressed;

		PopulateFpsLockOptions();
		ApplyTabTitles();
		InitializeAudioVolumeFromMasterBus();

		if (_sensitivitySlider is not null)
		{
			_sensitivitySlider.ValueChanged += _ => UpdateReadouts();
		}

		if (_fovSlider is not null)
		{
			_fovSlider.ValueChanged += _ => UpdateReadouts();
		}

		if (_masterVolumeSlider is not null)
		{
			_masterVolumeSlider.ValueChanged += _ => UpdateReadouts();
		}

		UpdateReadouts();
	}

	public void SetSettings(MenuSettings settings)
	{
		if (_sensitivitySlider is not null)
		{
			_sensitivitySlider.Value = settings.MouseSensitivity;
		}

		if (_invertLookY is not null)
		{
			_invertLookY.ButtonPressed = settings.InvertLookY;
		}

		if (_fovSlider is not null)
		{
			_fovSlider.Value = settings.LocalFov;
		}

		SetFpsLock(settings.FpsLock);
		UpdateReadouts();
	}

	private void OnApplyPressed()
	{
		ApplyMasterBusVolume();
		float sensitivity = _sensitivitySlider is null ? 0.0023f : (float)_sensitivitySlider.Value;
		bool invertLookY = _invertLookY is not null && _invertLookY.ButtonPressed;
		float localFov = _fovSlider is null ? 90.0f : (float)_fovSlider.Value;
		EmitSignal(SignalName.ApplyPressed, sensitivity, invertLookY, localFov, GetSelectedFpsLock());
	}

	private void OnBackPressed()
	{
		EmitSignal(SignalName.BackPressed);
	}

	private void UpdateReadouts()
	{
		if (_sensitivitySlider is not null && _sensitivityValue is not null)
		{
			_sensitivityValue.Text = $"{_sensitivitySlider.Value:0.0000}";
		}

		if (_fovSlider is not null && _fovValue is not null)
		{
			_fovValue.Text = $"{_fovSlider.Value:0}";
		}

		if (_masterVolumeSlider is not null && _masterVolumeValue is not null)
		{
			_masterVolumeValue.Text = $"{_masterVolumeSlider.Value * 100.0:0}%";
		}
	}

	private void PopulateFpsLockOptions()
	{
		if (_fpsLockOption is null)
		{
			return;
		}

		_fpsLockOption.Clear();
		_fpsLockOption.AddItem("Unlimited", 0);
		_fpsLockOption.AddItem("30", 30);
		_fpsLockOption.AddItem("60", 60);
		_fpsLockOption.AddItem("120", 120);
		_fpsLockOption.AddItem("144", 144);
		_fpsLockOption.AddItem("165", 165);
		_fpsLockOption.AddItem("240", 240);
	}

	private void ApplyTabTitles()
	{
		if (_tabs is null)
		{
			return;
		}

		_tabs.SetTabTitle(0, "Video");
		_tabs.SetTabTitle(1, "Controls");
		_tabs.SetTabTitle(2, "Audio");
	}

	private int GetSelectedFpsLock()
	{
		if (_fpsLockOption is null || _fpsLockOption.Selected < 0)
		{
			return 0;
		}

		return _fpsLockOption.GetItemId(_fpsLockOption.Selected);
	}

	private void SetFpsLock(int fpsLock)
	{
		if (_fpsLockOption is null)
		{
			return;
		}

		int target = fpsLock <= 0 ? 0 : fpsLock;
		for (int i = 0; i < _fpsLockOption.ItemCount; i++)
		{
			if (_fpsLockOption.GetItemId(i) == target)
			{
				_fpsLockOption.Selected = i;
				return;
			}
		}

		_fpsLockOption.Selected = 0;
	}

	private void InitializeAudioVolumeFromMasterBus()
	{
		if (_masterVolumeSlider is null)
		{
			return;
		}

		int busIndex = GetMasterBusIndex();
		if (busIndex < 0)
		{
			return;
		}

		float linear = Mathf.Clamp(Mathf.DbToLinear(AudioServer.GetBusVolumeDb(busIndex)), 0.0f, 1.0f);
		_masterVolumeSlider.Value = linear;
	}

	private void ApplyMasterBusVolume()
	{
		if (_masterVolumeSlider is null)
		{
			return;
		}

		int busIndex = GetMasterBusIndex();
		if (busIndex < 0)
		{
			return;
		}

		float linear = Mathf.Clamp((float)_masterVolumeSlider.Value, 0.0f, 1.0f);
		float db = linear <= 0.0001f ? -80.0f : Mathf.LinearToDb(linear);
		AudioServer.SetBusVolumeDb(busIndex, db);
	}

	private static int GetMasterBusIndex()
	{
		int masterIndex = AudioServer.GetBusIndex("Master");
		if (masterIndex >= 0)
		{
			return masterIndex;
		}

		return AudioServer.BusCount > 0 ? 0 : -1;
	}
}
