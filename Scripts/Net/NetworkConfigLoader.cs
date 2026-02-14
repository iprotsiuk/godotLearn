// Scripts/Net/NetworkConfigLoader.cs
using System.Text.Json;
using Godot;

namespace NetRunnerSlice.Net;

public static class NetworkConfigLoader
{
    public static NetworkConfig Load(string path)
    {
        if (!Godot.FileAccess.FileExists(path))
        {
            GD.PushWarning($"Network config not found at '{path}', using defaults.");
            return new NetworkConfig();
        }

        using Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        string json = file.GetAsText();

        try
        {
            NetworkConfig? loaded = JsonSerializer.Deserialize<NetworkConfig>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return loaded ?? new NetworkConfig();
        }
        catch (JsonException ex)
        {
            GD.PushError($"Failed to parse '{path}': {ex.Message}");
            return new NetworkConfig();
        }
    }
}
