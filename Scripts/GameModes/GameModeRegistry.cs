using System.Collections.Generic;

namespace NetRunnerSlice.GameModes;

public sealed class GameModeRegistry
{
    private readonly Dictionary<GameModeId, IGameMode> _modes;

    private GameModeRegistry(Dictionary<GameModeId, IGameMode> modes)
    {
        _modes = modes;
    }

    public static GameModeRegistry CreateDefault()
    {
        return new GameModeRegistry(new Dictionary<GameModeId, IGameMode>
        {
            { GameModeId.FreeRun, new FreeRunMode() },
            { GameModeId.TagClassic, new TagClassicMode() }
        });
    }

    public IGameMode Resolve(GameModeId modeId)
    {
        return _modes.TryGetValue(modeId, out IGameMode? mode)
            ? mode
            : _modes[GameModeId.FreeRun];
    }
}
