// Scripts/Net/NetSession.Weapon.cs
using System.Collections.Generic;
using Godot;
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
	}

	private readonly List<VisualProjectile> _visualProjectiles = new(32);
	private double _lastProjectileUpdateSec = -1.0;
	private CanvasLayer? _hitIndicatorLayer;
	private ColorRect? _hitIndicatorRect;
	private double _hitIndicatorExpireAtSec;
	private ColorRect? _damageIndicatorRect;
	private double _damageIndicatorExpireAtSec;

	private void TryFireWeapon()
	{
		if (!IsClient || _localCharacter is null)
		{
			return;
		}

		if (_mode == RunMode.Client && _localPeerId == 0)
		{
			return;
		}

		Vector3 aimDir = YawPitchToDirection(_lookYaw, _lookPitch).Normalized();
		int interpDelayTicks = GetGlobalInterpolationDelayTicks();
		uint fireTick = GetEstimatedServerTickNow();

		float globalInterpDelayMs = TicksToMs(interpDelayTicks);
		FireRequest request = new()
		{
			FireSeq = ++_nextFireSeq,
			FireTick = fireTick,
			InputEpoch = _inputEpoch,
			InterpDelayTicksUsed = interpDelayTicks,
			AimDirection = aimDir
		};
		uint targetTick = fireTick > (uint)interpDelayTicks ? fireTick - (uint)interpDelayTicks : 0;
		GD.Print($"FireRequest: peer={_localPeerId} seq={request.FireSeq} fireTick={fireTick} targetTick={targetTick} delayMs={globalInterpDelayMs:0.0} epoch={request.InputEpoch}");

		NetCodec.WriteFire(_firePacket, request);
		if (_mode == RunMode.ListenServer)
		{
			HandleFire(_localPeerId, _firePacket);
		}
		else
		{
			SendPacket(1, NetChannels.Control, MultiplayerPeer.TransferModeEnum.Reliable, _firePacket);
		}
	}

	private int GetGlobalInterpolationDelayTicks()
	{
		if (_globalInterpDelayTicks > 0)
		{
			return _globalInterpDelayTicks;
		}

		return MsToTicks(Mathf.Max(0.0f, _config.InterpolationDelayMs));
	}

	private void HandleFire(int fromPeer, byte[] packet)
	{
		if (!IsServer)
		{
			return;
		}

		GD.Print($"FireRecv: fromPeer={fromPeer} bytes={packet.Length} serverTick={_serverTick}");

		if (!_serverPlayers.TryGetValue(fromPeer, out ServerPlayer? shooter))
		{
			ServerPeerConnected(fromPeer);
			if (!_serverPlayers.TryGetValue(fromPeer, out shooter))
			{
				GD.Print($"FireReject: no server player for peer={fromPeer}");
				return;
			}
		}

		if (!NetCodec.TryReadFire(packet, out FireRequest request))
		{
			GD.Print($"FireReject: decode failed for peer={fromPeer}");
			return;
		}

		if (request.InputEpoch != shooter.CurrentInputEpoch)
		{
			GD.Print($"FireReject: epoch mismatch peer={fromPeer} reqEpoch={request.InputEpoch} curEpoch={shooter.CurrentInputEpoch}");
			return;
		}

		if (request.FireSeq <= shooter.LastProcessedFireSeq)
		{
			return;
		}
		shooter.LastProcessedFireSeq = request.FireSeq;

		uint oldestTick = _serverTick > (uint)(RewindHistoryTicks - 1)
			? _serverTick - (uint)(RewindHistoryTicks - 1)
			: 0;
		if (request.FireTick > _serverTick)
		{
			GD.Print($"FireReject: future fireTick={request.FireTick} serverTick={_serverTick}");
			return;
		}
		if (request.FireTick < oldestTick)
		{
			GD.Print($"FireReject: stale fireTick={request.FireTick} oldest={oldestTick} serverTick={_serverTick}");
			return;
		}

		int interpDelayTicks = Mathf.Clamp(request.InterpDelayTicksUsed, 0, RewindHistoryTicks - 1);
		uint targetTick = request.FireTick > (uint)interpDelayTicks
			? request.FireTick - (uint)interpDelayTicks
			: 0;
		if (targetTick < oldestTick)
		{
			targetTick = oldestTick;
		}

		Vector3 direction = request.AimDirection;
		if (direction.LengthSquared() <= 0.000001f)
		{
			return;
		}

		direction = direction.Normalized();
		Vector3 shooterPosAtFire = shooter.Character.GlobalPosition;
		TryGetHistoricalPosition(fromPeer, request.FireTick, out shooterPosAtFire);
		Vector3 shooterPosNow = shooter.Character.GlobalPosition;
		Vector3 shooterPosUsed = shooterPosAtFire;
		Vector3 origin = shooterPosUsed + new Vector3(0.0f, 1.55f, 0.0f);

		int hitPeer = -1;
		Vector3 hitPoint = origin + (direction * WeaponMaxRange);
		if (TryFindRayHitAtTick(fromPeer, targetTick, origin, direction, out int foundPeer, out Vector3 foundPoint))
		{
			hitPeer = foundPeer;
			hitPoint = foundPoint;
		}

		float shooterNowVsFire = shooterPosNow.DistanceTo(shooterPosAtFire);
		float shooterUsedVsFire = shooterPosUsed.DistanceTo(shooterPosAtFire);
		GD.Print(
			$"FireEval: serverTick={_serverTick} fireTick={request.FireTick} targetTick={targetTick} " +
			$"shooterNowDelta={shooterNowVsFire:0.###} shooterUsedDelta={shooterUsedVsFire:0.###}");

		FireResult result = new()
		{
			ShooterPeerId = fromPeer,
			HitPeerId = hitPeer,
			ValidatedServerTick = targetTick
		};
		if (hitPeer >= 0)
		{
			GD.Print($"ServerHit: shooter={fromPeer} target={hitPeer} fireTick={request.FireTick} targetTick={targetTick}");
		}
		else
		{
			GD.Print($"ServerMiss: shooter={fromPeer} fireTick={request.FireTick} targetTick={targetTick}");
		}
		NetCodec.WriteFireResult(_fireResultPacket, result);
		BroadcastFireResult(_fireResultPacket);
		FireVisual visual = new()
		{
			ShooterPeerId = fromPeer,
			ValidatedServerTick = targetTick,
			Origin = origin,
			Yaw = DirectionToYaw(direction),
			Pitch = DirectionToPitch(direction),
			HitPoint = hitPoint,
			DidHit = hitPeer >= 0
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
		if (_serverTick == 0)
		{
			return 0;
		}

		uint oldestTick = _serverTick > (uint)(RewindHistoryTicks - 1)
			? _serverTick - (uint)(RewindHistoryTicks - 1)
			: 0;
		if (requestedTick < oldestTick)
		{
			return oldestTick;
		}

		if (requestedTick > _serverTick)
		{
			return _serverTick;
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

	private bool TryFindRayHitAtTick(int shooterPeerId, uint tick, Vector3 origin, Vector3 direction, out int hitPeerId, out Vector3 hitPoint)
	{
		hitPeerId = -1;
		hitPoint = origin + (direction * WeaponMaxRange);
		float bestDistance = WeaponMaxRange + 0.001f;

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
			if (!TryRaySphereHit(origin, direction, center, WeaponTargetRadius, WeaponMaxRange, out float distance))
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

	private bool TryGetHistoricalPosition(int peerId, uint tick, out Vector3 position)
	{
		if (TryGetRewindPosition(peerId, tick, out position))
		{
			return true;
		}

		uint oldestTick = _serverTick > (uint)(RewindHistoryTicks - 1)
			? _serverTick - (uint)(RewindHistoryTicks - 1)
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
			ExpireAtSec = nowSec + VisualProjectileLifetimeSec
		});
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
				projectile.Node.GlobalPosition += projectile.Velocity * dt;
				_visualProjectiles[i] = projectile;
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
