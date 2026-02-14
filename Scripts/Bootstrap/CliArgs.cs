// Scripts/Bootstrap/CliArgs.cs
using Godot;

namespace NetRunnerSlice.Bootstrap;

public enum StartupRole
{
    None,
    Host,
    Join
}

public sealed class CliArgs
{
    public StartupRole Role { get; private set; } = StartupRole.None;

    public string Ip { get; private set; } = "127.0.0.1";

    public int Port { get; private set; } = 7777;

    public Vector2I? WindowPosition { get; private set; }

    public Vector2I? WindowSize { get; private set; }

    public bool SimulationEnabled { get; private set; }

    public int SimLatencyMs { get; private set; }

    public int SimJitterMs { get; private set; }

    public float SimLossPercent { get; private set; }

    public int SimSeed { get; private set; } = 1337;

    public static CliArgs Parse(string[] args)
    {
        CliArgs parsed = new();

        foreach (string raw in args)
        {
            if (!raw.StartsWith("--"))
            {
                continue;
            }

            string[] pair = raw.Substring(2).Split('=', 2);
            string key = pair[0].ToLowerInvariant();
            string value = pair.Length > 1 ? pair[1] : string.Empty;

            switch (key)
            {
                case "role":
                    parsed.Role = value.ToLowerInvariant() switch
                    {
                        "host" => StartupRole.Host,
                        "client" => StartupRole.Join,
                        "join" => StartupRole.Join,
                        _ => StartupRole.None
                    };
                    break;
                case "ip":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        parsed.Ip = value;
                    }
                    break;
                case "port":
                    if (int.TryParse(value, out int port))
                    {
                        parsed.Port = Mathf.Clamp(port, 1, 65535);
                    }
                    break;
                case "window-pos":
                    parsed.WindowPosition = ParseVec2I(value, parsed.WindowPosition);
                    break;
                case "window-size":
                    parsed.WindowSize = ParseVec2I(value, parsed.WindowSize);
                    break;
                case "sim-enable":
                    parsed.SimulationEnabled = value != "0";
                    break;
                case "sim-latency":
                    if (int.TryParse(value, out int latency))
                    {
                        parsed.SimLatencyMs = Mathf.Max(0, latency);
                    }
                    break;
                case "sim-jitter":
                    if (int.TryParse(value, out int jitter))
                    {
                        parsed.SimJitterMs = Mathf.Max(0, jitter);
                    }
                    break;
                case "sim-loss":
                    if (float.TryParse(value, out float loss))
                    {
                        parsed.SimLossPercent = Mathf.Clamp(loss, 0.0f, 100.0f);
                    }
                    break;
                case "sim-seed":
                    if (int.TryParse(value, out int seed))
                    {
                        parsed.SimSeed = seed;
                    }
                    break;
            }
        }

        return parsed;
    }

    public void ApplyWindow()
    {
        if (WindowSize.HasValue)
        {
            DisplayServer.WindowSetSize(WindowSize.Value);
        }

        if (WindowPosition.HasValue)
        {
            DisplayServer.WindowSetPosition(WindowPosition.Value);
        }
    }

    private static Vector2I? ParseVec2I(string raw, Vector2I? fallback)
    {
        string[] split = raw.Split(',', 2);
        if (split.Length != 2)
        {
            return fallback;
        }

        if (!int.TryParse(split[0], out int x) || !int.TryParse(split[1], out int y))
        {
            return fallback;
        }

        return new Vector2I(x, y);
    }
}
