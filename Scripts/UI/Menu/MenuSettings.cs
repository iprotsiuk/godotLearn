using Godot;

namespace NetRunnerSlice.UI.Menu;

public sealed class MenuSettings
{
	public float MouseSensitivity { get; set; } = 0.0023f;
	public bool InvertLookY { get; set; }
	public float LocalFov { get; set; } = 90.0f;

	public MenuSettings Clone()
	{
		return new MenuSettings
		{
			MouseSensitivity = MouseSensitivity,
			InvertLookY = InvertLookY,
			LocalFov = LocalFov
		};
	}
}

public static class MenuSettingsStore
{
	private const string SettingsPath = "user://settings.cfg";
	private const string SectionInput = "input";
	private const string SectionView = "view";

	public static MenuSettings Load()
	{
		MenuSettings defaults = new();
		ConfigFile file = new();
		Error err = file.Load(SettingsPath);
		if (err != Error.Ok)
		{
			return defaults;
		}

		float sensitivity = (float)file.GetValue(SectionInput, "mouse_sensitivity", defaults.MouseSensitivity);
		bool invertLookY = (bool)file.GetValue(SectionInput, "invert_look_y", defaults.InvertLookY);
		float localFov = (float)file.GetValue(SectionView, "local_fov", defaults.LocalFov);

		defaults.MouseSensitivity = Mathf.Clamp(sensitivity, 0.0005f, 0.02f);
		defaults.InvertLookY = invertLookY;
		defaults.LocalFov = Mathf.Clamp(localFov, 60.0f, 120.0f);
		return defaults;
	}

	public static void Save(MenuSettings settings)
	{
		ConfigFile file = new();
		file.SetValue(SectionInput, "mouse_sensitivity", Mathf.Clamp(settings.MouseSensitivity, 0.0005f, 0.02f));
		file.SetValue(SectionInput, "invert_look_y", settings.InvertLookY);
		file.SetValue(SectionView, "local_fov", Mathf.Clamp(settings.LocalFov, 60.0f, 120.0f));
		file.Save(SettingsPath);
	}
}
