// Scripts/GameModes/FreeRunMode.cs
using NetRunnerSlice.Match;
using NetRunnerSlice.Net;
using NetRunnerSlice.Player;

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

    public void ServerOnRoundStart(MatchManager matchManager, NetSession session)
    {
        session.SetCurrentTagStateFull(new TagState
        {
            RoundIndex = matchManager.RoundIndex,
            ItPeerId = -1,
            ItCooldownEndTick = 0
        }, broadcast: true);
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
