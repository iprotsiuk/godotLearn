// Scripts/GameModes/ModeStubs.cs
namespace NetRunnerSlice.GameModes;

public sealed class RaceModeStub : IGameMode
{
    public string Name => "Race (Stub)";

    public void Enter()
    {
        // TODO: Implement race scoring/checkpoints in a later phase.
    }

    public void Exit()
    {
    }
}

public sealed class TagModeStub : IGameMode
{
    public string Name => "Tag (Stub)";

    public void Enter()
    {
        // TODO: Implement tag rules in a later phase.
    }

    public void Exit()
    {
    }
}
