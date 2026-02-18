using Godot;
using NetRunnerSlice.GameModes;

namespace NetRunnerSlice.UI.Menu;

public sealed class MenuSettings
{
	public float MouseSensitivity { get; set; } = 0.0023f;
	public bool InvertLookY { get; set; }
	public float LocalFov { get; set; } = 90.0f;
	public int FpsLock { get; set; }
	public GameModeId SelectedMode { get; set; } = GameModeId.FreeRun;
	public int RoundTimeSec { get; set; } = 120;

	public MenuSettings Clone()
	{
			return new MenuSettings
			{
				MouseSensitivity = MouseSensitivity,
				InvertLookY = InvertLookY,
				LocalFov = LocalFov,
				FpsLock = FpsLock,
				SelectedMode = SelectedMode,
				RoundTimeSec = RoundTimeSec
			};
		}
	}

public static class MenuSettingsStore
{
	private const string SettingsPath = "user://settings.cfg";
	private const string SectionInput = "input";
	private const string SectionView = "view";
	private const string SectionGameplay = "gameplay";

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
			int fpsLock = (int)file.GetValue(SectionView, "fps_lock", defaults.FpsLock);
			int selectedModeRaw = (int)file.GetValue(SectionGameplay, "selected_mode", (int)defaults.SelectedMode);
			int roundTimeSec = (int)file.GetValue(SectionGameplay, "round_time_sec", defaults.RoundTimeSec);

			defaults.MouseSensitivity = Mathf.Clamp(sensitivity, 0.0005f, 0.02f);
			defaults.InvertLookY = invertLookY;
			defaults.LocalFov = Mathf.Clamp(localFov, 60.0f, 120.0f);
			defaults.FpsLock = fpsLock <= 0 ? 0 : fpsLock;
			defaults.SelectedMode = Enum.IsDefined(typeof(GameModeId), selectedModeRaw)
				? (GameModeId)selectedModeRaw
				: GameModeId.FreeRun;
			defaults.RoundTimeSec = ClampRoundTime(roundTimeSec);
			return defaults;
		}

	public static void Save(MenuSettings settings)
	{
		ConfigFile file = new();
			file.SetValue(SectionInput, "mouse_sensitivity", Mathf.Clamp(settings.MouseSensitivity, 0.0005f, 0.02f));
			file.SetValue(SectionInput, "invert_look_y", settings.InvertLookY);
			file.SetValue(SectionView, "local_fov", Mathf.Clamp(settings.LocalFov, 60.0f, 120.0f));
			file.SetValue(SectionView, "fps_lock", settings.FpsLock <= 0 ? 0 : settings.FpsLock);
			GameModeId mode = Enum.IsDefined(typeof(GameModeId), settings.SelectedMode)
				? settings.SelectedMode
				: GameModeId.FreeRun;
			file.SetValue(SectionGameplay, "selected_mode", (int)mode);
			file.SetValue(SectionGameplay, "round_time_sec", ClampRoundTime(settings.RoundTimeSec));
			file.Save(SettingsPath);
		}

	private static int ClampRoundTime(int roundTimeSec)
	{
		int clamped = Mathf.Clamp(roundTimeSec, 30, 900);
		return (clamped / 10) * 10;
	}
	}
