using Godot;

namespace NetRunnerSlice.UI.Menu;

public partial class MainMenuSettingsScreen : VBoxContainer
{
	[Signal]
	public delegate void ApplyPressedEventHandler(float mouseSensitivity, bool invertLookY, float localFov);

	[Signal]
	public delegate void BackPressedEventHandler();

	private HSlider? _sensitivitySlider;
	private Label? _sensitivityValue;
	private CheckBox? _invertLookY;
	private HSlider? _fovSlider;
	private Label? _fovValue;

	public override void _Ready()
	{
		_sensitivitySlider = GetNode<HSlider>("SensitivitySlider");
		_sensitivityValue = GetNode<Label>("SensitivityValue");
		_invertLookY = GetNode<CheckBox>("InvertLookY");
		_fovSlider = GetNode<HSlider>("FovSlider");
		_fovValue = GetNode<Label>("FovValue");

		GetNode<Button>("ApplyButton").Pressed += OnApplyPressed;
		GetNode<Button>("BackButton").Pressed += OnBackPressed;

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

		UpdateReadouts();
	}

	private void OnApplyPressed()
	{
		float sensitivity = _sensitivitySlider is null ? 0.0023f : (float)_sensitivitySlider.Value;
		bool invertLookY = _invertLookY is not null && _invertLookY.ButtonPressed;
		float localFov = _fovSlider is null ? 90.0f : (float)_fovSlider.Value;
		EmitSignal(SignalName.ApplyPressed, sensitivity, invertLookY, localFov);
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
}
