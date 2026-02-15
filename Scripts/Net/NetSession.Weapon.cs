// Scripts/Net/NetSession.Weapon.cs
using System.Collections.Generic;
using Godot;
using NetRunnerSlice.Player;

namespace NetRunnerSlice.Net;

public partial class NetSession
{
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

		Vector3 origin = _localCharacter.LocalCamera?.GlobalPosition ?? (_localCharacter.GlobalPosition + new Vector3(0.0f, 1.55f, 0.0f));
		FireRequest request = new()
		{
			EstimatedServerTickAtFire = EstimateServerTickAtFire(),
			InputEpoch = _inputEpoch,
			Origin = origin,
			Yaw = _lookYaw,
			Pitch = _lookPitch
		};

		NetCodec.WriteFire(_firePacket, request);
		if (_mode == RunMode.ListenServer)
		{
			HandleFire(_localPeerId, _firePacket);
		}
		else
		{
			SendPacket(1, NetChannels.Input, MultiplayerPeer.TransferModeEnum.Unreliable, _firePacket);
		}
	}

	private uint EstimateServerTickAtFire()
	{
		if (_mode == RunMode.ListenServer)
		{
			return _serverTick;
		}

		uint estimatedTick = _serverTick;
		if (_netClock is not null && _netClock.LastServerTick > 0)
		{
			double nowSec = Time.GetTicksMsec() / 1000.0;
			double estimatedServerTime = _netClock.GetEstimatedServerTime(nowSec);
			estimatedTick = (uint)Mathf.Max(0, Mathf.RoundToInt((float)(estimatedServerTime * _config.ServerTickRate)));
		}

		if (estimatedTick < _serverTick)
		{
			estimatedTick = _serverTick;
		}

		return estimatedTick;
	}

	private void HandleFire(int fromPeer, byte[] packet)
	{
		if (!IsServer)
		{
			return;
		}

		if (!_serverPlayers.TryGetValue(fromPeer, out ServerPlayer? shooter))
		{
			return;
		}

		if (!NetCodec.TryReadFire(packet, out FireRequest request))
		{
			return;
		}

		if (request.InputEpoch != shooter.CurrentInputEpoch)
		{
			return;
		}

		uint rewindTick = ClampRewindTick(request.EstimatedServerTickAtFire);
		Vector3 origin = ClampShotOrigin(fromPeer, rewindTick, request.Origin);
		Vector3 direction = YawPitchToDirection(request.Yaw, request.Pitch);
		if (direction.LengthSquared() <= 0.000001f)
		{
			return;
		}

		direction = direction.Normalized();
		int hitPeer = -1;
		Vector3 hitPoint = origin + (direction * WeaponMaxRange);

		_rewindRestoreScratch.Clear();
		try
		{
			ApplyRewindToTargets(fromPeer, rewindTick);
			if (TryFindRayHit(fromPeer, origin, direction, out int foundPeer, out Vector3 foundPoint))
			{
				hitPeer = foundPeer;
				hitPoint = foundPoint;
			}
		}
		finally
		{
			RestoreRewoundTargets();
		}

		FireResult result = new()
		{
			ShooterPeerId = fromPeer,
			HitPeerId = hitPeer,
			ValidatedServerTick = rewindTick
		};
		NetCodec.WriteFireResult(_fireResultPacket, result);
		BroadcastFireResult(_fireResultPacket);
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

	private bool TryFindRayHit(int shooterPeerId, Vector3 origin, Vector3 direction, out int hitPeerId, out Vector3 hitPoint)
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

			Vector3 center = pair.Value.Character.GlobalPosition + new Vector3(0.0f, 0.9f, 0.0f);
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
				TopLevel = true,
				GlobalPosition = end
			};
			marker.MaterialOverride = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				AlbedoColor = new Color(1.0f, 0.15f, 0.15f)
			};
			AddChild(marker);
			_debugDrawNodes.Add((marker, (Time.GetTicksMsec() / 1000.0) + 0.2));
		}
#endif
	}
}
