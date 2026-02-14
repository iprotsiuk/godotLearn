// Scripts/GameModes/FreeRunMode.cs
namespace NetRunnerSlice.GameModes;

public sealed class FreeRunMode : IGameMode
{
    public string Name => "FreeRun";

    public void Enter()
    {
        // Phase 1 mode: movement sandbox, no scoring.
    }

    public void Exit()
    {
    }
}
