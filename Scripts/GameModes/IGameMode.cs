// Scripts/GameModes/IGameMode.cs
namespace NetRunnerSlice.GameModes;

public interface IGameMode
{
    string Name { get; }

    void Enter();

    void Exit();
}
