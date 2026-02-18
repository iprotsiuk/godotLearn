// Scripts/GameModes/ModeStubs.cs
using NetRunnerSlice.Match;
using NetRunnerSlice.Net;
using NetRunnerSlice.Player;

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

    public void ServerOnRoundStart(MatchManager matchManager, NetSession session)
    {
    }

    public void ServerOnRoundEnd(MatchManager matchManager, NetSession session)
    {
    }

    public void ServerOnTick(MatchManager matchManager, NetSession session, uint tick)
    {
    }

    public void ClientOnTick(MatchManager matchManager, NetSession session, uint tick)
    {
    }

    public void ServerOnPostSimulatePlayer(
        MatchManager matchManager,
        NetSession session,
        int peerId,
        PlayerCharacter serverCharacter,
        InputCommand cmd,
        uint tick)
    {
    }

    public void ClientOnTagState(MatchManager matchManager, NetSession session, TagState state, bool isFull)
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

    public void ServerOnRoundStart(MatchManager matchManager, NetSession session)
    {
    }

    public void ServerOnRoundEnd(MatchManager matchManager, NetSession session)
    {
    }

    public void ServerOnTick(MatchManager matchManager, NetSession session, uint tick)
    {
    }

    public void ClientOnTick(MatchManager matchManager, NetSession session, uint tick)
    {
    }

    public void ServerOnPostSimulatePlayer(
        MatchManager matchManager,
        NetSession session,
        int peerId,
        PlayerCharacter serverCharacter,
        InputCommand cmd,
        uint tick)
    {
    }

    public void ClientOnTagState(MatchManager matchManager, NetSession session, TagState state, bool isFull)
    {
    }
}
