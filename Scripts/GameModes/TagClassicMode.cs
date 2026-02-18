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
    private static bool DronesEnabled => true;

    private const float DroneSpawnDelaySec = 15.0f;
    private const float DroneRespawnDelaySec = 5.0f;
    private const float DroneSpeedRatio = 0.30f;
    private const float DroneSteerLerpPerTick = 0.22f;
    private const float DroneVerticalGain = 2.0f;
    private const float DroneFreezeDurationSec = 2.0f;
    private const float DroneCatchRangeMeters = 1.35f;
    private const float DroneHoverHeightMeters = 1.35f;
    private const float DroneObstacleDetourMeters = 1.75f;
    private const uint DroneCollisionLayer = 1u << 2; // layer 3 bitmask; avoids player collisions on layer 1.
    private static readonly string[] DroneModelScenePaths =
    {
        "res://addons/guns/drone.glb",
        "res://addons/drone.glb"
    };
    private const uint DronePathRecalcTicks = 20;
    private const float DroneGridCellMeters = 1.0f;
    private const int DronePathPaddingCells = 18;
    private const int DronePathMaxExpandedNodes = 1200;

    private static readonly Vector2I[] DroneNeighborOffsets =
    {
        new(-1, -1), new(0, -1), new(1, -1),
        new(-1, 0),                new(1, 0),
        new(-1, 1),  new(0, 1),   new(1, 1)
    };

    private sealed class DroneAgent
    {
        public required int TargetPeerId;
        public required CharacterBody3D Body;
        public readonly List<Vector3> Path = new();
        public int PathIndex;
        public uint NextPathRecalcTick;
        public bool PendingRespawn;
        public uint RespawnAtTick;
    }

    private readonly RandomNumberGenerator _rng = new();
    private readonly Dictionary<int, DroneAgent> _dronesByRunner = new();
    private readonly HashSet<int> _runnerScratch = new();

    private int _itPeerId = -1;
    private uint _itTagCooldownUntilTick;
    private bool _droneSpawnTimerArmed;
    private uint _droneSpawnUnlockTick;
    private uint _lastDroneSimTick = uint.MaxValue;
    private bool _droneModelLoadAttempted;
    private PackedScene? _droneModelScene;
    private string _droneModelScenePathUsed = string.Empty;

    public string Name => "TagClassic";

    public void Enter()
    {
        _rng.Randomize();
        _itPeerId = -1;
        _itTagCooldownUntilTick = 0;
        _droneSpawnTimerArmed = false;
        _droneSpawnUnlockTick = 0;
        _lastDroneSimTick = uint.MaxValue;
    }

    public void Exit()
    {
        _itPeerId = -1;
        _itTagCooldownUntilTick = 0;
        _droneSpawnTimerArmed = false;
        _droneSpawnUnlockTick = 0;
        _lastDroneSimTick = uint.MaxValue;
        ClearAllDrones();
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
        _droneSpawnTimerArmed = false;
        _droneSpawnUnlockTick = 0;
        _lastDroneSimTick = uint.MaxValue;
        ClearAllDrones();

        session.SetCurrentTagStateFull(new TagState
        {
            RoundIndex = roundIndex,
            ItPeerId = _itPeerId,
            ItCooldownEndTick = _itTagCooldownUntilTick
        }, broadcast: true);
    }

    public void ServerOnRoundEnd(MatchManager matchManager, NetSession session)
    {
        ClearAllDrones();
        _droneSpawnTimerArmed = false;
        _droneSpawnUnlockTick = 0;
        _lastDroneSimTick = uint.MaxValue;
    }

    public void ServerOnTick(MatchManager matchManager, NetSession session, uint tick)
    {
        if (!DronesEnabled)
        {
            ClearAllDrones();
            _droneSpawnTimerArmed = false;
            _droneSpawnUnlockTick = 0;
            return;
        }

        if (tick == _lastDroneSimTick)
        {
            return;
        }

        _lastDroneSimTick = tick;

        if (matchManager.Phase != MatchPhase.Running || !HasValidIt(matchManager) || matchManager.RoundParticipants.Count < 2)
        {
            ClearAllDrones();
            _droneSpawnTimerArmed = false;
            _droneSpawnUnlockTick = 0;
            return;
        }

        if (!_droneSpawnTimerArmed)
        {
            _droneSpawnTimerArmed = true;
            uint delayTicks = (uint)Mathf.RoundToInt(DroneSpawnDelaySec * Mathf.Max(1, session.TickRate));
            _droneSpawnUnlockTick = tick + delayTicks;
        }

        BuildRunnerSetFromParticipants(matchManager);
        DespawnDronesForInvalidRunners();

        if (tick < _droneSpawnUnlockTick)
        {
            return;
        }

        EnsureRunnerDrones(session);
        UpdateDrones(session, tick, authoritative: true);
    }

    public void ClientOnTick(MatchManager matchManager, NetSession session, uint tick)
    {
        if (!DronesEnabled)
        {
            ClearAllDrones();
            session.ClearClientTagDroneStates();
            return;
        }

        if (session.IsServer)
        {
            return;
        }

        if (matchManager.Phase != MatchPhase.Running)
        {
            ClearAllDrones();
            session.ClearClientTagDroneStates();
            return;
        }

        int itPeerId = session.CurrentTagState.ItPeerId;
        if (itPeerId <= 0)
        {
            ClearAllDrones();
            session.ClearClientTagDroneStates();
            return;
        }

        _itPeerId = itPeerId;
        BuildRunnerSetFromKnownPeers(session, itPeerId);
        DespawnDronesForInvalidRunners();

        if (_runnerScratch.Count == 0)
        {
            ClearAllDrones();
            session.ClearClientTagDroneStates();
            return;
        }
        SyncClientDronesFromServer(session, tick);
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
        if (session.IsServer)
        {
            return;
        }

        _itPeerId = state.ItPeerId;
        _itTagCooldownUntilTick = state.ItCooldownEndTick;
        if (isFull)
        {
            session.ClearClientTagDroneStates();
        }
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

    private void BuildRunnerSetFromParticipants(MatchManager matchManager)
    {
        _runnerScratch.Clear();
        foreach (int peerId in matchManager.RoundParticipants)
        {
            if (peerId != _itPeerId)
            {
                _runnerScratch.Add(peerId);
            }
        }
    }

    private void BuildRunnerSetFromKnownPeers(NetSession session, int itPeerId)
    {
        _runnerScratch.Clear();
        foreach (int peerId in session.GetKnownPeerIds())
        {
            if (peerId > 0 && peerId != itPeerId)
            {
                _runnerScratch.Add(peerId);
            }
        }
    }

    private void EnsureRunnerDrones(NetSession session)
    {
        foreach (int runnerPeerId in _runnerScratch)
        {
            if (_dronesByRunner.ContainsKey(runnerPeerId))
            {
                continue;
            }

            DroneAgent? drone = SpawnDrone(session, runnerPeerId);
            if (drone is not null)
            {
                _dronesByRunner[runnerPeerId] = drone;
            }
        }
    }

    private DroneAgent? SpawnDrone(NetSession session, int runnerPeerId)
    {
        if (session.PlayerRoot is null)
        {
            return null;
        }

        CharacterBody3D body = new()
        {
            Name = $"Drone_{runnerPeerId}",
            MotionMode = CharacterBody3D.MotionModeEnum.Floating,
            CollisionLayer = DroneCollisionLayer,
            CollisionMask = 1,
            SafeMargin = 0.02f
        };

        CollisionShape3D collision = new();
        collision.Shape = new SphereShape3D { Radius = 0.35f };
        body.AddChild(collision);

        Node3D? droneVisual = CreateDroneVisualNode();
        if (droneVisual is not null)
        {
            body.AddChild(droneVisual);
        }
        else
        {
            MeshInstance3D mesh = new();
            mesh.Mesh = new SphereMesh { Radius = 0.32f, Height = 0.64f };
            mesh.MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.95f, 0.25f, 0.15f, 1.0f),
                EmissionEnabled = true,
                Emission = new Color(0.95f, 0.25f, 0.15f, 1.0f),
                EmissionEnergyMultiplier = 0.35f
            };
            body.AddChild(mesh);
        }

        session.PlayerRoot.AddChild(body);
        Vector3 spawn = session.SpawnOriginPosition;
        body.GlobalPosition = spawn + new Vector3(0.0f, DroneHoverHeightMeters, 0.0f);

        return new DroneAgent
        {
            TargetPeerId = runnerPeerId,
            Body = body,
            NextPathRecalcTick = (uint)_rng.RandiRange(0, (int)DronePathRecalcTicks - 1),
            PathIndex = 0
        };
    }

    private void DespawnDronesForInvalidRunners()
    {
        List<int> toRemove = new();
        foreach (KeyValuePair<int, DroneAgent> pair in _dronesByRunner)
        {
            if (_runnerScratch.Contains(pair.Key))
            {
                continue;
            }

            DespawnDrone(pair.Value);
            toRemove.Add(pair.Key);
        }

        foreach (int peerId in toRemove)
        {
            _dronesByRunner.Remove(peerId);
        }
    }

    private static void DespawnDrone(DroneAgent drone)
    {
        if (GodotObject.IsInstanceValid(drone.Body))
        {
            drone.Body.QueueFree();
        }
    }

    private void ClearAllDrones()
    {
        foreach (DroneAgent drone in _dronesByRunner.Values)
        {
            DespawnDrone(drone);
        }

        _dronesByRunner.Clear();
    }

    private void UpdateDrones(NetSession session, uint tick, bool authoritative)
    {
        if (_dronesByRunner.Count == 0)
        {
            return;
        }

        float speed = Mathf.Max(0.05f, session.MoveSpeed * DroneSpeedRatio);
        foreach (DroneAgent drone in _dronesByRunner.Values)
        {
            if (drone.PendingRespawn)
            {
                TryCompleteDroneRespawn(drone, session, tick);
                if (authoritative)
                {
                    session.ServerBroadcastTagDroneState(
                        drone.TargetPeerId,
                        tick,
                        drone.Body.GlobalPosition,
                        drone.Body.Velocity,
                        drone.Body.Visible && !drone.PendingRespawn);
                }
                continue;
            }

            if (!session.TryGetAnyCharacter(drone.TargetPeerId, out PlayerCharacter targetCharacter))
            {
                continue;
            }

            float desiredY = targetCharacter.GlobalPosition.Y + DroneHoverHeightMeters;
            Vector3 targetPos = targetCharacter.GlobalPosition + new Vector3(0.0f, DroneHoverHeightMeters, 0.0f);

            if (tick >= drone.NextPathRecalcTick)
            {
                RebuildDronePath(drone, session, targetCharacter, desiredY);
                drone.NextPathRecalcTick = tick + DronePathRecalcTicks;
            }

            Vector3 waypoint = ResolveDroneWaypoint(drone, targetCharacter, targetPos);
            Vector3 toWaypoint = waypoint - drone.Body.GlobalPosition;
            Vector2 toWaypointXZ = new(toWaypoint.X, toWaypoint.Z);
            Vector3 desiredVelocity = Vector3.Zero;
            if (toWaypointXZ.LengthSquared() > 0.000001f)
            {
                Vector2 desiredXZ = toWaypointXZ.Normalized() * speed;
                desiredVelocity.X = desiredXZ.X;
                desiredVelocity.Z = desiredXZ.Y;
            }

            // Keep vertical movement bounded to the same speed budget as horizontal chase speed.
            float maxVerticalSpeed = speed;
            float verticalError = desiredY - drone.Body.GlobalPosition.Y;
            desiredVelocity.Y = Mathf.Clamp(verticalError * DroneVerticalGain, -maxVerticalSpeed, maxVerticalSpeed);
            drone.Body.Velocity = drone.Body.Velocity.Lerp(desiredVelocity, DroneSteerLerpPerTick);

            drone.Body.MoveAndSlide();

            // If movement is blocked but target is still far, request an earlier path refresh.
            if (drone.Body.GetSlideCollisionCount() > 0 &&
                toWaypoint.LengthSquared() > 2.25f &&
                tick + 2 < drone.NextPathRecalcTick)
            {
                drone.NextPathRecalcTick = tick + 2;
            }

            bool reachedTarget = HasDroneReachedTarget(drone, targetCharacter);
            if (authoritative && reachedTarget)
            {
                session.ServerApplyFreeze(drone.TargetPeerId, DroneFreezeDurationSec);
                BeginDroneRespawnCooldown(drone, tick, session.TickRate);
                session.ServerBroadcastTagDroneState(drone.TargetPeerId, tick, drone.Body.GlobalPosition, drone.Body.Velocity, visible: false);
                continue;
            }

            if (drone.PathIndex < drone.Path.Count)
            {
                float waypointDistance = drone.Body.GlobalPosition.DistanceTo(drone.Path[drone.PathIndex]);
                if (waypointDistance <= 0.45f)
                {
                    drone.PathIndex++;
                }
            }

            if (authoritative)
            {
                session.ServerBroadcastTagDroneState(drone.TargetPeerId, tick, drone.Body.GlobalPosition, drone.Body.Velocity, drone.Body.Visible);
            }
        }
    }

    private void SyncClientDronesFromServer(NetSession session, uint clientEstimatedServerTick)
    {
        foreach (int runnerPeerId in _runnerScratch)
        {
            if (!session.TryGetClientTagDroneState(runnerPeerId, out TagDroneState droneState))
            {
                continue;
            }

            if (!_dronesByRunner.TryGetValue(runnerPeerId, out DroneAgent? drone))
            {
                drone = SpawnDrone(session, runnerPeerId);
                if (drone is null)
                {
                    continue;
                }

                drone.Body.CollisionLayer = 0;
                drone.Body.CollisionMask = 0;
                _dronesByRunner[runnerPeerId] = drone;
            }

            uint tickDelta = clientEstimatedServerTick >= droneState.ServerTick
                ? clientEstimatedServerTick - droneState.ServerTick
                : 0;
            uint maxExtrapTicks = (uint)Mathf.Clamp((int)(session.TickRate / 4), 0, 8);
            tickDelta = Math.Min(tickDelta, maxExtrapTicks);
            float extrapSec = tickDelta / (float)Mathf.Max(1, session.TickRate);
            Vector3 predictedPosition = droneState.Position + (droneState.Velocity * extrapSec);

            drone.Body.GlobalPosition = predictedPosition;
            drone.Body.Velocity = droneState.Velocity;
            drone.Body.Visible = droneState.Visible;
        }
    }

    private static Vector3 GetDroneWaypoint(DroneAgent drone, Vector3 fallbackTarget)
    {
        if (drone.PathIndex < drone.Path.Count)
        {
            return drone.Path[drone.PathIndex];
        }

        return fallbackTarget;
    }

    private Vector3 ResolveDroneWaypoint(DroneAgent drone, PlayerCharacter targetCharacter, Vector3 fallbackTarget)
    {
        if (drone.PathIndex < drone.Path.Count)
        {
            return drone.Path[drone.PathIndex];
        }

        World3D? world = drone.Body.GetWorld3D();
        if (world is null)
        {
            return fallbackTarget;
        }

        if (!TryGetObstacleHit(world.DirectSpaceState, drone, targetCharacter, fallbackTarget, out Vector3 hitPos, out Vector3 hitNormal))
        {
            return fallbackTarget;
        }

        Vector3 tangent = hitNormal.Cross(Vector3.Up);
        if (tangent.LengthSquared() <= 0.0001f)
        {
            tangent = Vector3.Right;
        }
        else
        {
            tangent = tangent.Normalized();
        }

        Vector3 leftCandidate = hitPos + (tangent * DroneObstacleDetourMeters);
        leftCandidate.Y = fallbackTarget.Y;
        Vector3 rightCandidate = hitPos - (tangent * DroneObstacleDetourMeters);
        rightCandidate.Y = fallbackTarget.Y;

        bool leftBlocked = TryGetObstacleHit(world.DirectSpaceState, drone, targetCharacter, leftCandidate, out _, out _);
        bool rightBlocked = TryGetObstacleHit(world.DirectSpaceState, drone, targetCharacter, rightCandidate, out _, out _);
        if (leftBlocked && rightBlocked)
        {
            return fallbackTarget;
        }

        if (leftBlocked)
        {
            return rightCandidate;
        }

        if (rightBlocked)
        {
            return leftCandidate;
        }

        return leftCandidate.DistanceTo(fallbackTarget) <= rightCandidate.DistanceTo(fallbackTarget)
            ? leftCandidate
            : rightCandidate;
    }

    private static bool HasDroneReachedTarget(DroneAgent drone, PlayerCharacter targetCharacter)
    {
        Vector3 dronePos = drone.Body.GlobalPosition;
        Vector3 targetCenter = targetCharacter.GlobalPosition + new Vector3(0.0f, 0.9f, 0.0f);
        if (dronePos.DistanceTo(targetCenter) <= DroneCatchRangeMeters)
        {
            return true;
        }

        Vector2 droneXZ = new(dronePos.X, dronePos.Z);
        Vector2 targetXZ = new(targetCenter.X, targetCenter.Z);
        float horizontalDistance = droneXZ.DistanceTo(targetXZ);
        float verticalDistance = Mathf.Abs(dronePos.Y - targetCenter.Y);
        return horizontalDistance <= DroneCatchRangeMeters && verticalDistance <= 1.6f;
    }

    private static bool TryGetObstacleHit(
        PhysicsDirectSpaceState3D space,
        DroneAgent drone,
        PlayerCharacter targetCharacter,
        in Vector3 destination,
        out Vector3 hitPosition,
        out Vector3 hitNormal)
    {
        PhysicsRayQueryParameters3D ray = new()
        {
            From = drone.Body.GlobalPosition,
            To = destination,
            CollisionMask = 1,
            CollideWithBodies = true,
            CollideWithAreas = false
        };

        Godot.Collections.Array<Rid> excludes = new();
        excludes.Add(drone.Body.GetRid());
        excludes.Add(targetCharacter.GetRid());
        ray.Exclude = excludes;

        Godot.Collections.Dictionary result = space.IntersectRay(ray);
        if (result.Count == 0)
        {
            hitPosition = Vector3.Zero;
            hitNormal = Vector3.Zero;
            return false;
        }

        hitPosition = result.TryGetValue("position", out Variant positionVariant)
            ? positionVariant.AsVector3()
            : Vector3.Zero;
        hitNormal = result.TryGetValue("normal", out Variant normalVariant)
            ? normalVariant.AsVector3()
            : Vector3.Up;
        return true;
    }

    private static void RespawnDrone(DroneAgent drone, Vector3 spawnOrigin)
    {
        drone.Body.GlobalPosition = spawnOrigin + new Vector3(0.0f, DroneHoverHeightMeters, 0.0f);
        drone.Body.Velocity = Vector3.Zero;
        drone.Body.Visible = true;
        drone.Body.CollisionLayer = DroneCollisionLayer;
        drone.Path.Clear();
        drone.PathIndex = 0;
        drone.NextPathRecalcTick = 0;
        drone.PendingRespawn = false;
        drone.RespawnAtTick = 0;
    }

    private static void BeginDroneRespawnCooldown(DroneAgent drone, uint tick, int tickRate)
    {
        drone.Body.Velocity = Vector3.Zero;
        drone.Body.Visible = false;
        drone.Body.CollisionLayer = 0;
        drone.Path.Clear();
        drone.PathIndex = 0;
        drone.NextPathRecalcTick = 0;
        drone.PendingRespawn = true;
        uint delayTicks = (uint)Mathf.RoundToInt(DroneRespawnDelaySec * Mathf.Max(1, tickRate));
        drone.RespawnAtTick = tick + delayTicks;
    }

    private static void TryCompleteDroneRespawn(DroneAgent drone, NetSession session, uint tick)
    {
        if (!drone.PendingRespawn || tick < drone.RespawnAtTick)
        {
            return;
        }

        RespawnDrone(drone, session.SpawnOriginPosition);
        drone.NextPathRecalcTick = tick + 1;
    }

    private void RebuildDronePath(DroneAgent drone, NetSession session, PlayerCharacter targetCharacter, float desiredY)
    {
        drone.Path.Clear();
        drone.PathIndex = 0;

        World3D? world = drone.Body.GetWorld3D();
        if (world is null)
        {
            return;
        }

        Vector3 start = drone.Body.GlobalPosition;
        Vector3 goal = targetCharacter.GlobalPosition + new Vector3(0.0f, DroneHoverHeightMeters, 0.0f);
        TryFindPath(
            world.DirectSpaceState,
            session,
            drone,
            targetCharacter,
            start,
            goal,
            desiredY,
            drone.Path);
    }

    private bool TryFindPath(
        PhysicsDirectSpaceState3D space,
        NetSession session,
        DroneAgent drone,
        PlayerCharacter targetCharacter,
        in Vector3 start,
        in Vector3 goal,
        float desiredY,
        List<Vector3> outPath)
    {
        Vector2I startCell = WorldToCell(start);
        Vector2I goalCell = WorldToCell(goal);

        int minX = Mathf.Min(startCell.X, goalCell.X) - DronePathPaddingCells;
        int maxX = Mathf.Max(startCell.X, goalCell.X) + DronePathPaddingCells;
        int minY = Mathf.Min(startCell.Y, goalCell.Y) - DronePathPaddingCells;
        int maxY = Mathf.Max(startCell.Y, goalCell.Y) + DronePathPaddingCells;

        List<Vector2I> open = new() { startCell };
        HashSet<Vector2I> closed = new();
        Dictionary<Vector2I, Vector2I> cameFrom = new();
        Dictionary<Vector2I, float> gScore = new() { [startCell] = 0.0f };
        Dictionary<Vector2I, float> fScore = new() { [startCell] = Heuristic(startCell, goalCell) };

        int expanded = 0;
        while (open.Count > 0 && expanded < DronePathMaxExpandedNodes)
        {
            expanded++;

            int currentIndex = 0;
            Vector2I current = open[0];
            float bestF = fScore.TryGetValue(current, out float score) ? score : float.MaxValue;
            for (int i = 1; i < open.Count; i++)
            {
                Vector2I candidate = open[i];
                float candidateF = fScore.TryGetValue(candidate, out float candidateScore)
                    ? candidateScore
                    : float.MaxValue;
                if (candidateF < bestF)
                {
                    bestF = candidateF;
                    current = candidate;
                    currentIndex = i;
                }
            }

            if (current == goalCell)
            {
                BuildPath(cameFrom, current, desiredY, outPath);
                outPath.Add(goal);
                return true;
            }

            open.RemoveAt(currentIndex);
            closed.Add(current);

            foreach (Vector2I offset in DroneNeighborOffsets)
            {
                Vector2I neighbor = current + offset;
                if (neighbor.X < minX || neighbor.X > maxX || neighbor.Y < minY || neighbor.Y > maxY)
                {
                    continue;
                }

                if (closed.Contains(neighbor))
                {
                    continue;
                }

                bool isGoal = neighbor == goalCell;
                if (!isGoal && !IsPathCellWalkable(space, session, drone, targetCharacter, neighbor, desiredY))
                {
                    continue;
                }

                bool diagonal = offset.X != 0 && offset.Y != 0;
                if (diagonal)
                {
                    Vector2I stepX = current + new Vector2I(offset.X, 0);
                    Vector2I stepY = current + new Vector2I(0, offset.Y);
                    bool stepXWalkable = stepX == goalCell || IsPathCellWalkable(space, session, drone, targetCharacter, stepX, desiredY);
                    bool stepYWalkable = stepY == goalCell || IsPathCellWalkable(space, session, drone, targetCharacter, stepY, desiredY);
                    if (!stepXWalkable || !stepYWalkable)
                    {
                        continue;
                    }
                }

                float moveCost = diagonal ? 1.4142135f : 1.0f;
                float currentG = gScore.TryGetValue(current, out float currentScore) ? currentScore : float.MaxValue;
                float tentativeG = currentG + moveCost;
                float neighborG = gScore.TryGetValue(neighbor, out float neighborScore) ? neighborScore : float.MaxValue;
                if (tentativeG >= neighborG)
                {
                    continue;
                }

                cameFrom[neighbor] = current;
                gScore[neighbor] = tentativeG;
                fScore[neighbor] = tentativeG + Heuristic(neighbor, goalCell);
                if (!open.Contains(neighbor))
                {
                    open.Add(neighbor);
                }
            }
        }

        outPath.Add(goal);
        return false;
    }

    private bool IsPathCellWalkable(
        PhysicsDirectSpaceState3D space,
        NetSession session,
        DroneAgent drone,
        PlayerCharacter targetCharacter,
        in Vector2I cell,
        float desiredY)
    {
        SphereShape3D probeShape = new() { Radius = 0.33f };
        PhysicsShapeQueryParameters3D query = new()
        {
            Shape = probeShape,
            Transform = new Transform3D(Basis.Identity, CellToWorld(cell, desiredY)),
            CollisionMask = 1,
            CollideWithBodies = true,
            CollideWithAreas = false,
            Margin = 0.01f
        };

        Godot.Collections.Array<Rid> excludes = new();
        excludes.Add(drone.Body.GetRid());
        excludes.Add(targetCharacter.GetRid());
        foreach (int peerId in session.GetKnownPeerIds())
        {
            if (session.TryGetAnyCharacter(peerId, out PlayerCharacter character))
            {
                excludes.Add(character.GetRid());
            }
        }

        foreach (DroneAgent otherDrone in _dronesByRunner.Values)
        {
            if (!ReferenceEquals(otherDrone, drone))
            {
                excludes.Add(otherDrone.Body.GetRid());
            }
        }

        query.Exclude = excludes;
        Godot.Collections.Array<Godot.Collections.Dictionary> hits = space.IntersectShape(query, 1);
        return hits.Count == 0;
    }

    private static void BuildPath(
        Dictionary<Vector2I, Vector2I> cameFrom,
        Vector2I current,
        float desiredY,
        List<Vector3> outPath)
    {
        List<Vector2I> reversed = new() { current };
        while (cameFrom.TryGetValue(current, out Vector2I parent))
        {
            reversed.Add(parent);
            current = parent;
        }

        for (int i = reversed.Count - 1; i >= 0; i--)
        {
            outPath.Add(CellToWorld(reversed[i], desiredY));
        }
    }

    private static Vector2I WorldToCell(in Vector3 worldPos)
    {
        return new Vector2I(
            Mathf.RoundToInt(worldPos.X / DroneGridCellMeters),
            Mathf.RoundToInt(worldPos.Z / DroneGridCellMeters));
    }

    private static Vector3 CellToWorld(in Vector2I cell, float y)
    {
        return new Vector3(
            cell.X * DroneGridCellMeters,
            y,
            cell.Y * DroneGridCellMeters);
    }

    private static float Heuristic(in Vector2I a, in Vector2I b)
    {
        int dx = Mathf.Abs(a.X - b.X);
        int dy = Mathf.Abs(a.Y - b.Y);
        return dx + dy;
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

    private Node3D? CreateDroneVisualNode()
    {
        if (!_droneModelLoadAttempted)
        {
            _droneModelLoadAttempted = true;
            foreach (string modelPath in DroneModelScenePaths)
            {
                _droneModelScene = ResourceLoader.Load<PackedScene>(modelPath);
                if (_droneModelScene is not null)
                {
                    _droneModelScenePathUsed = modelPath;
                    break;
                }
            }

            if (_droneModelScene is null)
            {
                GD.PushWarning(
                    $"Drone model not found at [{string.Join(", ", DroneModelScenePaths)}], using fallback sphere mesh.");
            }
        }

        if (_droneModelScene is null)
        {
            return null;
        }

        Node instantiated = _droneModelScene.Instantiate();
        if (instantiated is not Node3D visualRoot)
        {
            instantiated.QueueFree();
            GD.PushWarning($"Drone model root must be Node3D: {_droneModelScenePathUsed}. Using fallback sphere mesh.");
            _droneModelScene = null;
            return null;
        }

        visualRoot.Scale = new Vector3(0.2f, 0.2f, 0.2f);
        return visualRoot;
    }
}
