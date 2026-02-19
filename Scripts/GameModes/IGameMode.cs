// Scripts/GameModes/IGameMode.cs
using Godot;
using NetRunnerSlice.Match;
using NetRunnerSlice.Net;
using NetRunnerSlice.Player;

namespace NetRunnerSlice.GameModes;

public interface IGameMode
{
    string Name { get; }

    void Enter();

    void Exit();

    void ServerOnRoundStart(MatchManager matchManager, NetSession session);

    void ServerOnRoundEnd(MatchManager matchManager, NetSession session);

    void ServerOnTick(MatchManager matchManager, NetSession session, uint tick);

    void ClientOnTick(MatchManager matchManager, NetSession session, uint tick);

    void ServerOnPostSimulatePlayer(
        MatchManager matchManager,
        NetSession session,
        int peerId,
        PlayerCharacter serverCharacter,
        InputCommand cmd,
        uint tick);

    void ClientOnTagState(MatchManager matchManager, NetSession session, TagState state, bool isFull);

    bool ServerTryHandleFreezeGunShot(
        MatchManager matchManager,
        NetSession session,
        int shooterPeerId,
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        uint tick,
        out Vector3 hitPoint);
}
