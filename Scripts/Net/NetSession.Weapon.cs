// Scripts/Net/NetSession.Weapon.cs
using System.Collections.Generic;
using Godot;
using NetRunnerSlice.Items;
using NetRunnerSlice.Player;

namespace NetRunnerSlice.Net;

public partial class NetSession
{
	private const float VisualProjectileSpeed = 75.0f;
	private const double VisualProjectileLifetimeSec = 0.75;
	private const double HitIndicatorDurationSec = 0.12;
	private const double DamageFlashDurationSec = 0.15;

	private sealed class VisualProjectile
	{
		public required MeshInstance3D Node;
		public Vector3 Velocity;
		public double ExpireAtSec;
		public bool HasTargetPoint;
		public Vector3 TargetPoint;
	}

	private readonly List<VisualProjectile> _visualProjectiles = new(32);
	private double _lastProjectileUpdateSec = -1.0;
	private CanvasLayer? _hitIndicatorLayer;
	private ColorRect? _hitIndicatorRect;
	private double _hitIndicatorExpireAtSec;
	private ColorRect? _damageIndicatorRect;
	private double _damageIndicatorExpireAtSec;

	private void HandleFire(int fromPeer, byte[] packet)
	{
		if (!IsServer)
		{
			return;
		}

		// Keep legacy decode path for compatibility while authoritative fire comes from InputCommand.
		if (!NetCodec.TryReadFire(packet, out FireRequest request))
		{
			return;
		}

		GD.Print(
			$"FireLegacyIgnored: peer={fromPeer} fireTick={request.FireTick} seq={request.FireSeq} " +
			$"reason=fire_integrated_into_input_command");
	}

	private void ProcessFireFromInputCommand(int shooterPeerId, ServerPlayer shooter, in InputCommand command)
	{
		if ((command.Buttons & InputButtons.FirePressed) == 0)
		{
			return;
		}

		if (shooter.EquippedItem == ItemId.None)
		{
			return;
		}

		bool isFreezeGun = shooter.EquippedItem == ItemId.FreezeGun;
		if (isFreezeGun)
		{
			if (shooter.EquippedCharges == 0)
			{
				ServerClearEquippedItem(shooterPeerId);
				return;
			}

			if (command.InputTick < shooter.EquippedCooldownEndTick)
			{
				return;
			}

			shooter.EquippedCooldownEndTick = command.InputTick + (uint)(3 * TickRate);
			shooter.EquippedCharges--;
			if (shooter.EquippedCharges == 0)
			{
				ServerClearEquippedItem(shooterPeerId);
			}
			else
			{
				BroadcastInventoryStateForPeer(shooterPeerId);
			}
		}

		uint fireTick = command.InputTick;
		uint oldestTick = _server_sim_tick > (uint)(RewindHistoryTicks - 1)
			? _server_sim_tick - (uint)(RewindHistoryTicks - 1)
			: 0;
		int interpDelayTicks = Mathf.Clamp(MsToTicks(Mathf.Max(0.0f, _config.InterpolationDelayMs)), 0, RewindHistoryTicks - 1);
		uint targetTick = fireTick > (uint)interpDelayTicks
			? fireTick - (uint)interpDelayTicks
			: 0;
		if (targetTick < oldestTick)
		{
			targetTick = oldestTick;
		}

		// Aim direction is world-space and derived from the same yaw/pitch sampled for fireTick.
		Vector3 rayDirection = YawPitchToDirection(command.Yaw, command.Pitch).Normalized();
		Vector3 viewAtTickDirection = YawPitchToDirection(command.Yaw, command.Pitch).Normalized();
		float angleDiffDeg = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(viewAtTickDirection.Dot(rayDirection), -1.0f, 1.0f)));

		Vector3 shooterPosAtFire = shooter.Character.GlobalPosition;
		TryGetHistoricalPosition(shooterPeerId, fireTick, out shooterPosAtFire);
		Vector3 origin = ClampShotOrigin(shooterPeerId, fireTick, shooterPosAtFire + new Vector3(0.0f, 1.55f, 0.0f));

		int hitPeer = -1;
		float maxShotDistance = WeaponMaxRange;
		Vector3 hitPoint = origin + (rayDirection * WeaponMaxRange);
		bool blockedByWorld = false;
		if (TryGetWorldRayBlockDistance(shooterPeerId, origin, rayDirection, out float worldBlockDistance, out Vector3 worldBlockPoint))
		{
			maxShotDistance = Mathf.Max(0.0f, worldBlockDistance);
			hitPoint = worldBlockPoint;
			blockedByWorld = true;
		}

		if (TryFindRayHitAtTick(shooterPeerId, targetTick, origin, rayDirection, maxShotDistance, out int foundPeer, out Vector3 foundPoint))
		{
			hitPeer = foundPeer;
			hitPoint = foundPoint;
		}

		int tickAlignment = (int)_server_sim_tick - (int)fireTick;
		GD.Print(
			$"FireEval: serverTick={_server_sim_tick} fireTick={fireTick} targetTick={targetTick} " +
			$"tick_alignment={tickAlignment} angle_diff_deg={angleDiffDeg:0.###}");

		if (hitPeer < 0 &&
			TryComputeClosestApproachAtTick(
				shooterPeerId,
				command.Yaw,
				targetTick,
				origin,
				rayDirection,
				out int closestPeer,
				out float closestDist,
				out float signedLateralError))
		{
			Vector3 shooterVelocityAtFire = shooter.Character.Velocity;
			GD.Print(
				$"FireBiasDiag: shooter={shooterPeerId} nearest={closestPeer} tick={fireTick} " +
				$"shooter_velocity={shooterVelocityAtFire} closest_approach={closestDist:0.###} signed_lateral_error={signedLateralError:0.###}");
		}

		FireResult result = new()
		{
			ShooterPeerId = shooterPeerId,
			HitPeerId = hitPeer,
			ValidatedServerTick = targetTick
		};
		if (hitPeer >= 0)
		{
			if (isFreezeGun)
			{
				ServerApplyFreeze(hitPeer, 1.0f);
			}
			else
			{
				ApplyWeaponDamage(hitPeer, WeaponHitDamage);
			}
			GD.Print($"ServerHit: shooter={shooterPeerId} target={hitPeer} fireTick={fireTick} targetTick={targetTick}");
		}
		else
		{
			GD.Print($"ServerMiss: shooter={shooterPeerId} fireTick={fireTick} targetTick={targetTick}");
		}

		NetCodec.WriteFireResult(_fireResultPacket, result);
		BroadcastFireResult(_fireResultPacket);

		FireVisual visual = new()
		{
			ShooterPeerId = shooterPeerId,
			ValidatedServerTick = targetTick,
			Origin = origin,
			Yaw = DirectionToYaw(rayDirection),
			Pitch = DirectionToPitch(rayDirection),
			HitPoint = hitPoint,
			DidHit = hitPeer >= 0 || blockedByWorld
		};
		NetCodec.WriteFireVisual(_fireVisualPacket, visual);
		BroadcastFireVisual(_fireVisualPacket);
		DrawDebugShot(origin, hitPoint, hitPeer >= 0);
	}

	private void HandleFireResult(byte[] packet)
	{
		if (!IsClient)
		{
			return;
		}

		if (!NetCodec.TryReadFireResult(packet, out FireResult result))
		{
			return;
		}

		if (result.HitPeerId >= 0)
		{
			GD.Print($"FireResult: shooter={result.ShooterPeerId} hit={result.HitPeerId} tick={result.ValidatedServerTick}");
		}
		else
		{
			GD.Print($"FireResult: shooter={result.ShooterPeerId} miss tick={result.ValidatedServerTick}");
		}

		if (result.ShooterPeerId == _localPeerId && _localPeerId != 0)
		{
			int interpDelayTicks = Mathf.Clamp(MsToTicks(Mathf.Max(0.0f, _config.InterpolationDelayMs)), 0, RewindHistoryTicks - 1);
			uint fireTick = result.ValidatedServerTick + (uint)interpDelayTicks;
			if (TryConsumeLocalFirePressDiag(fireTick, out FirePressDiagSample sample))
			{
				double dtMs = ((long)Time.GetTicksUsec() - sample.LocalUsec) / 1000.0;
				GD.Print(
					$"FireLatencyDiag: fireTick={fireTick} validatedTick={result.ValidatedServerTick} dt_ms={dtMs:0.0} " +
					$"appliedDelayTicks={sample.AppliedDelayTicks} targetDelayTicks={sample.TargetDelayTicks} " +
					$"rtt={sample.RttMs:0.0} jitter={sample.JitterMs:0.0} horizon_gap={sample.HorizonGap}");
			}
			else
			{
				GD.Print($"FireLatencyDiag: missMapping fireTick={fireTick} validatedTick={result.ValidatedServerTick}");
			}

			ShowHitIndicator(result.HitPeerId >= 0);
		}

		if (result.HitPeerId == _localPeerId && result.ShooterPeerId != _localPeerId)
		{
			ShowDamageIndicator();
		}
	}

	private void BroadcastFireResult(byte[] packet)
	{
		foreach (int targetPeer in _serverPlayers.Keys)
		{
			if (_mode == RunMode.ListenServer && targetPeer == _localPeerId)
			{
				HandleFireResult(packet);
				continue;
			}

			SendPacket(targetPeer, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, packet);
		}
	}

	private void HandleFireVisual(byte[] packet)
	{
		if (!IsClient)
		{
			return;
		}

		if (!NetCodec.TryReadFireVisual(packet, out FireVisual visual))
		{
			return;
		}

		if (visual.ShooterPeerId == _localPeerId && _localCharacter is not null)
		{
			return;
		}

		Vector3 direction = YawPitchToDirection(visual.Yaw, visual.Pitch);
		if (direction.LengthSquared() <= 0.000001f)
		{
			return;
		}

		direction = direction.Normalized();
		Vector3 targetPoint = visual.DidHit
			? visual.HitPoint
			: visual.Origin + (direction * WeaponMaxRange);
		SpawnRemoteProjectile(visual.Origin, targetPoint);
	}

	private void BroadcastFireVisual(byte[] packet)
	{
		foreach (int targetPeer in _serverPlayers.Keys)
		{
			if (_mode == RunMode.ListenServer && targetPeer == _localPeerId)
			{
				HandleFireVisual(packet);
				continue;
			}

			SendPacket(targetPeer, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, packet);
		}
	}

	private uint ClampRewindTick(uint requestedTick)
	{
		if (_server_sim_tick == 0)
		{
			return 0;
		}

		uint oldestTick = _server_sim_tick > (uint)(RewindHistoryTicks - 1)
			? _server_sim_tick - (uint)(RewindHistoryTicks - 1)
			: 0;
		if (requestedTick < oldestTick)
		{
			return oldestTick;
		}

		if (requestedTick > _server_sim_tick)
		{
			return _server_sim_tick;
		}

		return requestedTick;
	}

	private Vector3 ClampShotOrigin(int shooterPeerId, uint rewindTick, Vector3 requestedOrigin)
	{
		if (!_serverPlayers.TryGetValue(shooterPeerId, out ServerPlayer? shooter))
		{
			return requestedOrigin;
		}

		Vector3 shooterPos = shooter.Character.GlobalPosition;
		if (TryGetRewindPosition(shooterPeerId, rewindTick, out Vector3 rewoundShooter))
		{
			shooterPos = rewoundShooter;
		}

		Vector3 expectedOrigin = shooterPos + new Vector3(0.0f, 1.55f, 0.0f);
		if (requestedOrigin.DistanceTo(expectedOrigin) > WeaponOriginMaxOffset)
		{
			return expectedOrigin;
		}

		return requestedOrigin;
	}

	private static Vector3 YawPitchToDirection(float yaw, float pitch)
	{
		float cosPitch = Mathf.Cos(pitch);
		return new Vector3(
			-Mathf.Sin(yaw) * cosPitch,
			Mathf.Sin(pitch),
			-Mathf.Cos(yaw) * cosPitch);
	}

	private void ApplyRewindToTargets(int shooterPeerId, uint rewindTick)
	{
		foreach (KeyValuePair<int, ServerPlayer> pair in _serverPlayers)
		{
			int peerId = pair.Key;
			if (peerId == shooterPeerId)
			{
				continue;
			}

			PlayerCharacter target = pair.Value.Character;
			Vector3 original = target.GlobalPosition;
			if (TryGetRewindPosition(peerId, rewindTick, out Vector3 rewound))
			{
				target.GlobalPosition = rewound;
			}

			_rewindRestoreScratch.Add((target, original));
		}
	}

	private void RestoreRewoundTargets()
	{
		foreach ((PlayerCharacter character, Vector3 position) in _rewindRestoreScratch)
		{
			character.GlobalPosition = position;
		}

		_rewindRestoreScratch.Clear();
	}

	private bool TryFindRayHitAtTick(int shooterPeerId, uint tick, Vector3 origin, Vector3 direction, float maxDistance, out int hitPeerId, out Vector3 hitPoint)
	{
		hitPeerId = -1;
		hitPoint = origin + (direction * maxDistance);
		float bestDistance = maxDistance + 0.001f;

		foreach (KeyValuePair<int, ServerPlayer> pair in _serverPlayers)
		{
			int peerId = pair.Key;
			if (peerId == shooterPeerId)
			{
				continue;
			}

			if (pair.Value.HealthCurrent <= 0)
			{
				continue;
			}

			Vector3 targetPos = pair.Value.Character.GlobalPosition;
			TryGetHistoricalPosition(peerId, tick, out targetPos);
			Vector3 center = targetPos + new Vector3(0.0f, 0.9f, 0.0f);
			if (!TryRaySphereHit(origin, direction, center, WeaponTargetRadius, maxDistance, out float distance))
			{
				continue;
			}

			if (distance >= bestDistance)
			{
				continue;
			}

			bestDistance = distance;
			hitPeerId = peerId;
			hitPoint = origin + (direction * distance);
		}

		return hitPeerId >= 0;
	}

	private bool TryGetWorldRayBlockDistance(int shooterPeerId, Vector3 origin, Vector3 direction, out float blockDistance, out Vector3 blockPoint)
	{
		blockDistance = WeaponMaxRange;
		blockPoint = origin + (direction * WeaponMaxRange);

		if (!_serverPlayers.TryGetValue(shooterPeerId, out ServerPlayer? shooter))
		{
			return false;
		}

		World3D? world = shooter.Character.GetWorld3D();
		if (world is null)
		{
			return false;
		}

		PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, blockPoint);
		query.CollideWithAreas = false;
		query.CollideWithBodies = true;

		Godot.Collections.Array<Rid> exclude = new();
		foreach (KeyValuePair<int, ServerPlayer> pair in _serverPlayers)
		{
			exclude.Add(pair.Value.Character.GetRid());
		}

		query.Exclude = exclude;
		Godot.Collections.Dictionary hit = world.DirectSpaceState.IntersectRay(query);
		if (hit.Count == 0 || !hit.ContainsKey("position"))
		{
			return false;
		}

		blockPoint = (Vector3)hit["position"];
		blockDistance = origin.DistanceTo(blockPoint);
		return true;
	}

	private void ApplyWeaponDamage(int targetPeerId, int damage)
	{
		if (damage <= 0 || !_serverPlayers.TryGetValue(targetPeerId, out ServerPlayer? target))
		{
			return;
		}

		int nextHealth = Mathf.Max(0, target.HealthCurrent - damage);
		target.HealthCurrent = nextHealth;
	}

	private bool TryComputeClosestApproachAtTick(
		int shooterPeerId,
		float shooterYawAtFireTick,
		uint tick,
		Vector3 origin,
		Vector3 direction,
		out int closestPeerId,
		out float closestDistance,
		out float signedLateralError)
	{
		closestPeerId = -1;
		closestDistance = float.MaxValue;
		signedLateralError = 0.0f;

		Vector3 shooterRight = new Vector3(Mathf.Cos(shooterYawAtFireTick), 0.0f, -Mathf.Sin(shooterYawAtFireTick)).Normalized();
		foreach (KeyValuePair<int, ServerPlayer> pair in _serverPlayers)
		{
			int peerId = pair.Key;
			if (peerId == shooterPeerId)
			{
				continue;
			}

			Vector3 targetPos = pair.Value.Character.GlobalPosition;
			TryGetHistoricalPosition(peerId, tick, out targetPos);
			Vector3 center = targetPos + new Vector3(0.0f, 0.9f, 0.0f);
			float t = Mathf.Clamp((center - origin).Dot(direction), 0.0f, WeaponMaxRange);
			Vector3 closestPoint = origin + (direction * t);
			Vector3 delta = center - closestPoint;
			float dist = delta.Length();
			if (dist >= closestDistance)
			{
				continue;
			}

			closestDistance = dist;
			closestPeerId = peerId;
			signedLateralError = delta.Dot(shooterRight);
		}

		return closestPeerId >= 0;
	}

	private bool TryGetHistoricalPosition(int peerId, uint tick, out Vector3 position)
	{
		if (TryGetRewindPosition(peerId, tick, out position))
		{
			return true;
		}

		uint oldestTick = _server_sim_tick > (uint)(RewindHistoryTicks - 1)
			? _server_sim_tick - (uint)(RewindHistoryTicks - 1)
			: 0;
		for (uint scanTick = tick; scanTick >= oldestTick; scanTick--)
		{
			if (TryGetRewindPosition(peerId, scanTick, out position))
			{
				return true;
			}
			if (scanTick == 0)
			{
				break;
			}
		}

		position = Vector3.Zero;
		return false;
	}

	private static float DirectionToYaw(Vector3 direction)
	{
		return Mathf.Atan2(-direction.X, -direction.Z);
	}

	private static float DirectionToPitch(Vector3 direction)
	{
		return Mathf.Asin(Mathf.Clamp(direction.Y, -1.0f, 1.0f));
	}

	private static bool TryRaySphereHit(
		Vector3 rayOrigin,
		Vector3 rayDirection,
		Vector3 sphereCenter,
		float radius,
		float maxDistance,
		out float hitDistance)
	{
		hitDistance = 0.0f;

		Vector3 m = rayOrigin - sphereCenter;
		float b = m.Dot(rayDirection);
		float c = m.Dot(m) - (radius * radius);
		if (c > 0.0f && b > 0.0f)
		{
			return false;
		}

		float discriminant = (b * b) - c;
		if (discriminant < 0.0f)
		{
			return false;
		}

		float t = -b - Mathf.Sqrt(discriminant);
		if (t < 0.0f)
		{
			t = 0.0f;
		}

		if (t > maxDistance)
		{
			return false;
		}

		hitDistance = t;
		return true;
	}

	private void RecordRewindFrame(uint tick)
	{
		int historyIndex = (int)(tick % RewindHistoryTicks);
		_rewindTicks[historyIndex] = tick;

		int count = 0;
		foreach (KeyValuePair<int, ServerPlayer> pair in _serverPlayers)
		{
			if (count >= NetConstants.MaxPlayers)
			{
				break;
			}

			_rewindSamples[historyIndex, count] = new RewindSample
			{
				PeerId = pair.Key,
				Position = pair.Value.Character.GlobalPosition
			};
			count++;
		}

		_rewindCounts[historyIndex] = count;
	}

	private bool TryGetRewindPosition(int peerId, uint tick, out Vector3 position)
	{
		position = Vector3.Zero;
		int historyIndex = (int)(tick % RewindHistoryTicks);
		if (_rewindTicks[historyIndex] != tick)
		{
			return false;
		}

		int count = _rewindCounts[historyIndex];
		for (int i = 0; i < count; i++)
		{
			RewindSample sample = _rewindSamples[historyIndex, i];
			if (sample.PeerId != peerId)
			{
				continue;
			}

			position = sample.Position;
			return true;
		}

		return false;
	}

	private void UpdateDebugDraws(double nowSec)
	{
		UpdateProjectileVisuals(nowSec);
		UpdateHitIndicator(nowSec);
		UpdateDamageIndicator(nowSec);

		for (int i = 0; i < _debugDrawNodes.Count;)
		{
			(Node3D node, double expireAt) = _debugDrawNodes[i];
			if (expireAt > nowSec)
			{
				i++;
				continue;
			}

			if (GodotObject.IsInstanceValid(node))
			{
				node.QueueFree();
			}

			int last = _debugDrawNodes.Count - 1;
			_debugDrawNodes[i] = _debugDrawNodes[last];
			_debugDrawNodes.RemoveAt(last);
		}
	}

	private void SpawnLocalProjectile(Vector3 origin, Vector3 direction, Vector3 shooterVelocity)
	{
		if (_mode == RunMode.None)
		{
			return;
		}

		origin += direction * 0.25f;

		MeshInstance3D projectile = new()
		{
			Mesh = new SphereMesh { Radius = 0.05f, Height = 0.1f },
			TopLevel = true
		};
		projectile.MaterialOverride = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor = new Color(0.2f, 0.95f, 1.0f)
		};

		AddChild(projectile);
		projectile.GlobalPosition = origin;
		double nowSec = Time.GetTicksMsec() / 1000.0;
		_visualProjectiles.Add(new VisualProjectile
		{
			Node = projectile,
			Velocity = (direction * VisualProjectileSpeed) + shooterVelocity,
			ExpireAtSec = nowSec + VisualProjectileLifetimeSec
		});
	}

	private void TrySpawnPredictedLocalFireVisual()
	{
		if (!IsClient || _localCharacter is null)
		{
			return;
		}

		if (GetLocalEquippedItemForClientView() == ItemId.None)
		{
			return;
		}

		Camera3D? camera = _localCharacter.LocalCamera;
		if (camera is null || !GodotObject.IsInstanceValid(camera))
		{
			return;
		}

		Transform3D cameraTransform = camera.GlobalTransform;
		Vector3 origin = cameraTransform.Origin;
		Vector3 direction = -cameraTransform.Basis.Z;
		if (direction.LengthSquared() <= 0.000001f)
		{
			return;
		}

		direction = direction.Normalized();
		Vector3 targetPoint = origin + (direction * WeaponMaxRange);
		if (TryGetLocalVisualWorldImpactPoint(origin, direction, out Vector3 wallPoint))
		{
			targetPoint = wallPoint;
		}

		SpawnRemoteProjectile(origin, targetPoint);
	}

	private void SpawnRemoteProjectile(Vector3 origin, Vector3 hitPoint)
	{
		Vector3 direction = hitPoint - origin;
		if (direction.LengthSquared() <= 0.000001f)
		{
			return;
		}

		direction = direction.Normalized();
		MeshInstance3D projectile = new()
		{
			Mesh = new SphereMesh { Radius = 0.05f, Height = 0.1f },
			TopLevel = true
		};
		projectile.MaterialOverride = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor = new Color(1.0f, 0.65f, 0.2f)
		};

		AddChild(projectile);
		projectile.GlobalPosition = origin + (direction * 0.25f);
		double nowSec = Time.GetTicksMsec() / 1000.0;
		_visualProjectiles.Add(new VisualProjectile
		{
			Node = projectile,
			Velocity = direction * VisualProjectileSpeed,
			ExpireAtSec = nowSec + VisualProjectileLifetimeSec,
			HasTargetPoint = true,
			TargetPoint = hitPoint
		});
	}

	private bool TryGetLocalVisualWorldImpactPoint(Vector3 origin, Vector3 direction, out Vector3 impactPoint)
	{
		impactPoint = origin + (direction * WeaponMaxRange);
		if (_localCharacter is null)
		{
			return false;
		}

		World3D? world = _localCharacter.GetWorld3D();
		if (world is null)
		{
			return false;
		}

		PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, impactPoint);
		query.CollideWithAreas = false;
		query.CollideWithBodies = true;
		query.Exclude = new Godot.Collections.Array<Rid> { _localCharacter.GetRid() };
		Godot.Collections.Dictionary hit = world.DirectSpaceState.IntersectRay(query);
		if (hit.Count == 0 || !hit.ContainsKey("position"))
		{
			return false;
		}

		impactPoint = (Vector3)hit["position"];
		return true;
	}

	private void UpdateProjectileVisuals(double nowSec)
	{
		if (_lastProjectileUpdateSec < 0.0)
		{
			_lastProjectileUpdateSec = nowSec;
			return;
		}

		float dt = Mathf.Max(0.0f, (float)(nowSec - _lastProjectileUpdateSec));
		_lastProjectileUpdateSec = nowSec;
		for (int i = 0; i < _visualProjectiles.Count;)
		{
			VisualProjectile projectile = _visualProjectiles[i];
			bool expired = projectile.ExpireAtSec <= nowSec || !GodotObject.IsInstanceValid(projectile.Node);
			if (!expired)
			{
				Vector3 currentPos = projectile.Node.GlobalPosition;
				Vector3 nextPos = currentPos + (projectile.Velocity * dt);
				if (projectile.HasTargetPoint)
				{
					Vector3 toTarget = projectile.TargetPoint - currentPos;
					Vector3 step = nextPos - currentPos;
					bool reachedTarget = step.Dot(toTarget) >= 0.0f && step.LengthSquared() >= toTarget.LengthSquared();
					if (reachedTarget)
					{
						nextPos = projectile.TargetPoint;
						expired = true;
					}
				}

				projectile.Node.GlobalPosition = nextPos;
				_visualProjectiles[i] = projectile;
			}
			if (!expired)
			{
				i++;
				continue;
			}
			if (GodotObject.IsInstanceValid(projectile.Node))
			{
				projectile.Node.QueueFree();
			}

			int last = _visualProjectiles.Count - 1;
			_visualProjectiles[i] = _visualProjectiles[last];
			_visualProjectiles.RemoveAt(last);
		}
	}

	private void ClearProjectileVisuals()
	{
		foreach (VisualProjectile projectile in _visualProjectiles)
		{
			if (GodotObject.IsInstanceValid(projectile.Node))
			{
				projectile.Node.QueueFree();
			}
		}

		_visualProjectiles.Clear();
		_lastProjectileUpdateSec = -1.0;
	}

	private void EnsureHitIndicator()
	{
		if (_hitIndicatorLayer is not null && GodotObject.IsInstanceValid(_hitIndicatorLayer))
		{
			return;
		}

		_hitIndicatorLayer = new CanvasLayer { Layer = 50 };
		AddChild(_hitIndicatorLayer);

		_hitIndicatorRect = new ColorRect
		{
			AnchorLeft = 0.5f,
			AnchorTop = 0.5f,
			AnchorRight = 0.5f,
			AnchorBottom = 0.5f,
			OffsetLeft = -6.0f,
			OffsetTop = -6.0f,
			OffsetRight = 6.0f,
			OffsetBottom = 6.0f,
			Color = new Color(0.2f, 1.0f, 0.2f, 0.0f),
			Visible = false,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		_hitIndicatorLayer.AddChild(_hitIndicatorRect);

		_damageIndicatorRect = new ColorRect
		{
			AnchorLeft = 0.0f,
			AnchorTop = 0.0f,
			AnchorRight = 1.0f,
			AnchorBottom = 1.0f,
			OffsetLeft = 0.0f,
			OffsetTop = 0.0f,
			OffsetRight = 0.0f,
			OffsetBottom = 0.0f,
			Color = new Color(1.0f, 0.12f, 0.12f, 0.0f),
			Visible = false,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		_hitIndicatorLayer.AddChild(_damageIndicatorRect);
	}

	private void ShowHitIndicator(bool hit)
	{
		EnsureHitIndicator();
		if (_hitIndicatorRect is null)
		{
			return;
		}

		_hitIndicatorRect.Color = hit
			? new Color(0.2f, 1.0f, 0.2f, 0.95f)
			: new Color(1.0f, 0.25f, 0.25f, 0.95f);
		_hitIndicatorRect.Visible = true;
		_hitIndicatorExpireAtSec = (Time.GetTicksMsec() / 1000.0) + HitIndicatorDurationSec;
	}

	private void UpdateHitIndicator(double nowSec)
	{
		if (_hitIndicatorRect is null || !_hitIndicatorRect.Visible)
		{
			return;
		}

		if (nowSec < _hitIndicatorExpireAtSec)
		{
			return;
		}

		_hitIndicatorRect.Visible = false;
	}

	private void ShowDamageIndicator()
	{
		EnsureHitIndicator();
		if (_damageIndicatorRect is null)
		{
			return;
		}

		_damageIndicatorRect.Color = new Color(1.0f, 0.12f, 0.12f, 0.22f);
		_damageIndicatorRect.Visible = true;
		_damageIndicatorExpireAtSec = (Time.GetTicksMsec() / 1000.0) + DamageFlashDurationSec;
	}

	private void UpdateDamageIndicator(double nowSec)
	{
		if (_damageIndicatorRect is null || !_damageIndicatorRect.Visible)
		{
			return;
		}

		if (nowSec < _damageIndicatorExpireAtSec)
		{
			return;
		}

		_damageIndicatorRect.Visible = false;
	}

	private void ClearHitIndicator()
	{
		if (_hitIndicatorRect is not null && GodotObject.IsInstanceValid(_hitIndicatorRect))
		{
			_hitIndicatorRect.Visible = false;
		}

		if (_hitIndicatorLayer is not null && GodotObject.IsInstanceValid(_hitIndicatorLayer))
		{
			_hitIndicatorLayer.QueueFree();
		}

		_hitIndicatorRect = null;
		_damageIndicatorRect = null;
		_hitIndicatorLayer = null;
		_hitIndicatorExpireAtSec = 0.0;
		_damageIndicatorExpireAtSec = 0.0;
	}

	private void DrawDebugShot(Vector3 start, Vector3 end, bool didHit)
	{
#if DEBUG
		ImmediateMesh mesh = new();
		mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
		mesh.SurfaceAddVertex(start);
		mesh.SurfaceAddVertex(end);
		mesh.SurfaceEnd();

		MeshInstance3D line = new()
		{
			Mesh = mesh,
			TopLevel = true
		};
		line.MaterialOverride = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor = didHit ? new Color(0.95f, 0.25f, 0.25f) : new Color(0.95f, 0.95f, 0.25f)
		};
		AddChild(line);
		_debugDrawNodes.Add((line, (Time.GetTicksMsec() / 1000.0) + 0.18));

		if (didHit)
		{
			MeshInstance3D marker = new()
			{
				Mesh = new SphereMesh { Radius = 0.08f, Height = 0.16f },
				TopLevel = true
			};
			marker.MaterialOverride = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				AlbedoColor = new Color(1.0f, 0.15f, 0.15f)
			};
			AddChild(marker);
			marker.GlobalPosition = end;
			_debugDrawNodes.Add((marker, (Time.GetTicksMsec() / 1000.0) + 0.2));
		}
#endif
	}
}
