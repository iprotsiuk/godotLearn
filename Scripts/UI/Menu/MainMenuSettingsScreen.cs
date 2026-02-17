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

	public override void _Ready()
	{
			_sensitivitySlider = GetNode<HSlider>("SensitivityRow/SensitivitySlider");
			_sensitivityValue = GetNode<Label>("SensitivityRow/SensitivityValue");
			_invertLookY = GetNode<CheckBox>("InvertLookY");
			_fovSlider = GetNode<HSlider>("FovRow/FovSlider");
			_fovValue = GetNode<Label>("FovRow/FovValue");
			_fpsLockOption = GetNode<OptionButton>("FpsLockOption");

			GetNode<Button>("ApplyButton").Pressed += OnApplyPressed;
			GetNode<Button>("BackButton").Pressed += OnBackPressed;

			PopulateFpsLockOptions();
			_sensitivitySlider.ValueChanged += _ => UpdateReadouts();
			_fovSlider.ValueChanged += _ => UpdateReadouts();
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
	}
