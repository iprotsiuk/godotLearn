using System;
using System.Collections.Generic;
using Godot;
using NetRunnerSlice.Match;
using NetRunnerSlice.Net;
using NetRunnerSlice.Player;

namespace NetRunnerSlice.GameModes;

public sealed class TagClassicMode : IGameMode
{
    private const float TagRangeMeters = 1.0f;
    private const float AimDotThreshold = 0.90f;
    private const float CloseAssistRangeMeters = 1.25f;

    private readonly RandomNumberGenerator _rng = new();
    private int _itPeerId = -1;
    private uint _itTagCooldownUntilTick;

    public string Name => "TagClassic";

    public void Enter()
    {
        _rng.Randomize();
        _itPeerId = -1;
        _itTagCooldownUntilTick = 0;
    }

    public void Exit()
    {
        _itPeerId = -1;
        _itTagCooldownUntilTick = 0;
    }

    public void ServerOnRoundStart(MatchManager matchManager, NetSession session)
    {
        int roundIndex = matchManager.RoundIndex;
        if (matchManager.RoundParticipants.Count < 2)
        {
            _itPeerId = -1;
            _itTagCooldownUntilTick = 0;
            session.SetCurrentTagStateFull(new TagState
            {
                RoundIndex = roundIndex,
                ItPeerId = _itPeerId,
                ItCooldownEndTick = _itTagCooldownUntilTick
            }, broadcast: true);
            return;
        }

        List<int> candidates = new(matchManager.RoundParticipants.Count);
        foreach (int peerId in matchManager.RoundParticipants)
        {
            candidates.Add(peerId);
        }

        int chosenIndex = (int)_rng.RandiRange(0, candidates.Count - 1);
        _itPeerId = candidates[chosenIndex];
        _itTagCooldownUntilTick = 0;

        session.SetCurrentTagStateFull(new TagState
        {
            RoundIndex = roundIndex,
            ItPeerId = _itPeerId,
            ItCooldownEndTick = _itTagCooldownUntilTick
        }, broadcast: true);
    }

    public void ServerOnRoundEnd(MatchManager matchManager, NetSession session)
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
        if (matchManager.Phase != MatchPhase.Running)
        {
            return;
        }

        if (!HasValidIt(matchManager) || peerId != _itPeerId)
        {
            return;
        }

        if ((cmd.Buttons & InputButtons.InteractPressed) == 0)
        {
            return;
        }

        if (tick < _itTagCooldownUntilTick)
        {
            return;
        }

        if (matchManager.RoundParticipants.Count < 2)
        {
            return;
        }

        if (!TryFindTagTarget(matchManager, session, serverCharacter, cmd, out int targetPeerId))
        {
            return;
        }

        ApplyTagTransfer(session, targetPeerId, tick);
    }

    public void ClientOnTagState(MatchManager matchManager, NetSession session, TagState state, bool isFull)
    {
    }

    private bool HasValidIt(MatchManager matchManager)
    {
        if (matchManager.RoundParticipants.Count < 2)
        {
            _itPeerId = -1;
            _itTagCooldownUntilTick = 0;
            return false;
        }

        if (_itPeerId > 0 && matchManager.RoundParticipants.Contains(_itPeerId))
        {
            return true;
        }

        _itPeerId = -1;
        _itTagCooldownUntilTick = 0;
        return false;
    }

    private bool TryFindTagTarget(
        MatchManager matchManager,
        NetSession session,
        PlayerCharacter itCharacter,
        in InputCommand cmd,
        out int targetPeerId)
    {
        targetPeerId = -1;

        Vector3 itPosition = itCharacter.GlobalPosition + new Vector3(0.0f, 1.5f, 0.0f);
        Vector3 forward = ComputeForward(cmd.Yaw, cmd.Pitch);

        float bestDot = -1.0f;
        float bestDistanceSq = float.MaxValue;

        foreach (int peerId in matchManager.RoundParticipants)
        {
            if (peerId == _itPeerId)
            {
                continue;
            }

            if (!session.TryGetServerCharacter(peerId, out PlayerCharacter targetCharacter))
            {
                continue;
            }

            Vector3 toTarget = (targetCharacter.GlobalPosition + new Vector3(0.0f, 1.1f, 0.0f)) - itPosition;
            float distanceSq = toTarget.LengthSquared();
            if (distanceSq <= 0.000001f)
            {
                targetPeerId = peerId;
                return true;
            }

            float maxRangeSq = TagRangeMeters * TagRangeMeters;
            if (distanceSq > maxRangeSq)
            {
                continue;
            }

            Vector3 toTargetDir = toTarget / Mathf.Sqrt(distanceSq);
            float dot = forward.Dot(toTargetDir);
            bool inCloseAssistRange = distanceSq <= (CloseAssistRangeMeters * CloseAssistRangeMeters);
            if (dot < AimDotThreshold && !inCloseAssistRange)
            {
                continue;
            }

            bool betterDot = dot > bestDot + 0.0001f;
            bool tieOnDot = Mathf.Abs(dot - bestDot) <= 0.0001f;
            bool betterDistance = distanceSq < bestDistanceSq;
            if (betterDot || (tieOnDot && betterDistance))
            {
                bestDot = dot;
                bestDistanceSq = distanceSq;
                targetPeerId = peerId;
            }
        }

        return targetPeerId > 0;
    }

    private void ApplyTagTransfer(NetSession session, int taggedPeerId, uint tick)
    {
        _itPeerId = taggedPeerId;
        session.RespawnServerPeerAtSpawn(taggedPeerId);

        uint cooldownTicks = (uint)(3 * session.TickRate);
        _itTagCooldownUntilTick = tick + cooldownTicks;
        session.SetCurrentTagStateDelta(_itPeerId, _itTagCooldownUntilTick, broadcast: true);
    }

    private static Vector3 ComputeForward(float yaw, float pitch)
    {
        float cosPitch = Mathf.Cos(pitch);
        Vector3 forward = new(
            -Mathf.Sin(yaw) * cosPitch,
            Mathf.Sin(pitch),
            -Mathf.Cos(yaw) * cosPitch);
        return forward.Normalized();
    }
}
