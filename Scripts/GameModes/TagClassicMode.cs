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
    private const float DroneAccelXZ = 60.0f;
    private const float DroneAccelY = 80.0f;
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
    private const uint DronePathRecalcTicks = 1;
    private const int DroneWaypointLookAheadPoints = 6;

    private sealed class DroneAgent
    {
        public required int TargetPeerId;
        public required CharacterBody3D Body;
        public readonly List<Vector3> Path = new();
        public int PathIndex;
        public uint NextPathRecalcTick;
        public bool PendingRespawn;
        public uint RespawnAtTick;
        public Vector3 LastPos;
        public int NoProgressTicks;
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
            PathIndex = 0,
            LastPos = body.GlobalPosition,
            NoProgressTicks = 0
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
        float dt = 1.0f / Mathf.Max(1, session.TickRate);
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
            Vector2 desiredXZ = Vector2.Zero;
            if (toWaypointXZ.LengthSquared() > 0.000001f)
            {
                desiredXZ = toWaypointXZ.Normalized() * speed;
            }

            Vector2 currentXZ = new(drone.Body.Velocity.X, drone.Body.Velocity.Z);
            Vector2 steeredXZ = currentXZ.MoveToward(desiredXZ, DroneAccelXZ * dt);
            if (steeredXZ.LengthSquared() > (speed * speed))
            {
                steeredXZ = steeredXZ.Normalized() * speed;
            }

            // Keep vertical movement bounded to the same speed budget as horizontal chase speed.
            float maxVerticalSpeed = speed;
            float verticalError = desiredY - drone.Body.GlobalPosition.Y;
            float desiredVerticalVelocity = Mathf.Clamp(verticalError * DroneVerticalGain, -maxVerticalSpeed, maxVerticalSpeed);
            float steeredY = Mathf.MoveToward(drone.Body.Velocity.Y, desiredVerticalVelocity, DroneAccelY * dt);
            drone.Body.Velocity = new Vector3(steeredXZ.X, steeredY, steeredXZ.Y);

            drone.Body.MoveAndSlide();
            if (authoritative)
            {
                Vector3 pos = drone.Body.GlobalPosition;
                float movedSq = (pos - drone.LastPos).LengthSquared();
                if (movedSq < 0.0001f && drone.Body.GetSlideCollisionCount() > 0)
                {
                    drone.NoProgressTicks++;
                }
                else
                {
                    drone.NoProgressTicks = 0;
                }

                if (drone.NoProgressTicks >= 8)
                {
                    drone.NextPathRecalcTick = tick + 1;
                    drone.PathIndex = Mathf.Min(drone.PathIndex + 1, drone.Path.Count);
                    drone.NoProgressTicks = 0;
                }

                drone.LastPos = pos;
            }

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
        World3D? world = drone.Body.GetWorld3D();
        if (world is null)
        {
            if (drone.PathIndex < drone.Path.Count)
            {
                return drone.Path[drone.PathIndex];
            }

            return fallbackTarget;
        }

        if (!TryGetObstacleHit(world.DirectSpaceState, drone, targetCharacter, fallbackTarget, out Vector3 hitPos, out Vector3 hitNormal))
        {
            if (drone.PathIndex < drone.Path.Count)
            {
                drone.PathIndex = drone.Path.Count;
            }

            return fallbackTarget;
        }

        if (TryGetFurthestVisiblePathWaypoint(world.DirectSpaceState, drone, targetCharacter, out Vector3 visibleWaypoint))
        {
            return visibleWaypoint;
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

    private bool TryGetFurthestVisiblePathWaypoint(
        PhysicsDirectSpaceState3D space,
        DroneAgent drone,
        PlayerCharacter targetCharacter,
        out Vector3 waypoint)
    {
        waypoint = Vector3.Zero;
        if (drone.PathIndex >= drone.Path.Count)
        {
            return false;
        }

        int startIndex = Mathf.Clamp(drone.PathIndex, 0, drone.Path.Count - 1);
        int maxIndex = Mathf.Min(drone.Path.Count - 1, startIndex + DroneWaypointLookAheadPoints);
        int furthestVisibleIndex = -1;
        for (int i = startIndex; i <= maxIndex; i++)
        {
            Vector3 candidate = drone.Path[i];
            bool blocked = TryGetObstacleHit(space, drone, targetCharacter, candidate, out _, out _);
            if (!blocked)
            {
                furthestVisibleIndex = i;
            }
        }

        if (furthestVisibleIndex < 0)
        {
            return false;
        }

        drone.PathIndex = furthestVisibleIndex;
        waypoint = drone.Path[furthestVisibleIndex];
        return true;
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
        drone.LastPos = drone.Body.GlobalPosition;
        drone.NoProgressTicks = 0;
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
        drone.LastPos = drone.Body.GlobalPosition;
        drone.NoProgressTicks = 0;
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
        TryBuildNavPath(world, start, goal, drone.Path);
    }

    private static bool TryBuildNavPath(World3D world, in Vector3 start, in Vector3 goal, List<Vector3> outPath)
    {
        Rid navigationMap = world.NavigationMap;
        if (!navigationMap.IsValid)
        {
            outPath.Add(goal);
            return false;
        }

        Vector3[] navPath = NavigationServer3D.MapGetPath(navigationMap, start, goal, optimize: true);
        if (navPath.Length == 0)
        {
            outPath.Add(goal);
            return false;
        }

        outPath.AddRange(navPath);
        if (outPath[^1].DistanceTo(goal) > 0.25f)
        {
            outPath.Add(goal);
        }

        return true;
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
