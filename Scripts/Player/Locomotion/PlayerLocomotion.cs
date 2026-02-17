// Scripts/Player/Locomotion/PlayerLocomotion.cs
using Godot;
using NetRunnerSlice.Net;

namespace NetRunnerSlice.Player.Locomotion;

public readonly struct LocomotionStepResult
{
	public readonly bool WasGrounded;
	public readonly bool IsOnFloor;
	public readonly float FloorSnapLength;
	public readonly LocomotionMode Mode;

	public LocomotionStepResult(bool wasGrounded, bool isOnFloor, float floorSnapLength, LocomotionMode mode)
	{
		WasGrounded = wasGrounded;
		IsOnFloor = isOnFloor;
		FloorSnapLength = floorSnapLength;
		Mode = mode;
	}
}

public static class PlayerLocomotion
{
	private readonly struct WallRunStepResult
	{
		public static WallRunStepResult None => new(forceExitWallRun: false, didWallJump: false, cooldownTicks: 0);

		public readonly bool ForceExitWallRun;
		public readonly bool DidWallJump;
		public readonly int CooldownTicks;

		public WallRunStepResult(bool forceExitWallRun, bool didWallJump, int cooldownTicks)
		{
			ForceExitWallRun = forceExitWallRun;
			DidWallJump = didWallJump;
			CooldownTicks = cooldownTicks;
		}
	}

	private const float LedgeSupportProbeExtra = 0.05f;
	private const float WallContactMaxAbsY = 0.2f;
	private const float WallRunIntentMinMoveAxesSq = 0.01f;
	private const float MinNormalLengthSq = 0.000001f;
	private const float WallRunStickSpeed = 2.0f;
	private const float WallProbeChestHeight = 1.05f;
	private const float WallProbeSideOffset = 0.2f;
	private const float WallProbeDistance = 0.8f;
	private const int WallRunReattachCooldownTicks = 8;
	private static readonly bool EnableWallRun = true;
	private static readonly bool EnableWallCling = false;
	private static readonly bool EnableSlide = false;

	public static LocomotionStepResult Step(PlayerCharacter player, CharacterBody3D body, in InputCommand input, NetworkConfig config)
	{
		LocomotionState state = player.GetLocomotionState();
		Vector3 velocity = body.Velocity;
		bool wasGrounded = body.IsOnFloor();
		if (player.TryConsumeGroundedOverride(out bool groundedOverride))
		{
			wasGrounded = groundedOverride;
		}

		bool jumpPressed = (input.Buttons & InputButtons.JumpPressed) != 0;
		LocomotionMode activeMode = ResolveActiveMode(state.Mode, wasGrounded);
		WallRunStepResult wallRunResult = WallRunStepResult.None;

		switch (activeMode)
		{
			case LocomotionMode.Grounded:
				GroundedStep(player, input, config, ref velocity, jumpPressed, wasGrounded);
				break;
			case LocomotionMode.Air:
				AirStep(input, config, ref velocity, wasGrounded);
				break;
			case LocomotionMode.WallRun:
				wallRunResult = WallRunStep(player, input, config, ref velocity, state, jumpPressed);
				if (wallRunResult.ForceExitWallRun)
				{
					state.WallNormal = Vector3.Zero;
				}
				break;
			case LocomotionMode.WallCling:
				WallClingStep(input, config, ref velocity, wasGrounded);
				break;
			case LocomotionMode.Slide:
				SlideStep(input, config, ref velocity, wasGrounded);
				break;
			default:
				AirStep(input, config, ref velocity, wasGrounded);
				break;
		}

		bool hasSupportAhead = HasSupportAhead(body, input.DtFixed, config.FloorSnapLength, wasGrounded, velocity);
		bool allowSnap = wasGrounded && !jumpPressed && velocity.Y <= 0.0f && hasSupportAhead;
		body.FloorSnapLength = allowSnap ? config.FloorSnapLength : 0.0f;

		body.Velocity = velocity;
		body.MoveAndSlide();

		bool groundedAfter = body.IsOnFloor();
		Vector3 wallNormalCandidate = ComputeWallNormalCandidate(body, WallContactMaxAbsY, state.WallNormal, input, velocity);
		bool hasWallContact = wallNormalCandidate != Vector3.Zero;
		if (activeMode == LocomotionMode.Air && state.WallRunTicksRemaining > 0)
		{
			state.WallRunTicksRemaining = Mathf.Max(0, state.WallRunTicksRemaining - 1);
		}

		LocomotionMode nextMode;
		if (groundedAfter)
		{
			nextMode = LocomotionMode.Grounded;
			state.WallNormal = Vector3.Zero;
			state.WallRunTicksRemaining = 0;
		}
		else if (wallRunResult.ForceExitWallRun)
		{
			nextMode = LocomotionMode.Air;
			state.WallNormal = Vector3.Zero;
			state.WallRunTicksRemaining = Mathf.Max(0, wallRunResult.CooldownTicks);
		}
		else if (activeMode == LocomotionMode.WallRun)
		{
			int remainingWallRunTicks = Mathf.Max(0, state.WallRunTicksRemaining - 1);
			if (!hasWallContact)
			{
				nextMode = LocomotionMode.Air;
				state.WallNormal = Vector3.Zero;
				state.WallRunTicksRemaining = 0;
			}
			else if (remainingWallRunTicks <= 0)
			{
				nextMode = LocomotionMode.Air;
				state.WallNormal = Vector3.Zero;
				state.WallRunTicksRemaining = WallRunReattachCooldownTicks;
			}
			else
			{
				nextMode = LocomotionMode.WallRun;
				state.WallNormal = wallNormalCandidate;
				state.WallRunTicksRemaining = remainingWallRunTicks;
			}
		}
		else if (state.WallRunTicksRemaining <= 0 &&
				 activeMode == LocomotionMode.Air &&
				 hasWallContact &&
				 config.WallRunMaxTicks > 0 &&
				 IsWallRunIntentActive(input))
		{
			nextMode = LocomotionMode.WallRun;
			state.WallNormal = wallNormalCandidate;
			state.WallRunTicksRemaining = Mathf.Clamp(config.WallRunMaxTicks, 0, 255);
		}
		else
		{
			nextMode = LocomotionMode.Air;
			state.WallNormal = Vector3.Zero;
		}

		UpdateModeAndCounters(ref state, nextMode);
		player.SetLocomotionState(state);
		player.PostSimUpdate();

		return new LocomotionStepResult(wasGrounded, groundedAfter, body.FloorSnapLength, state.Mode);
	}

	private static Vector3 ComputeWallNormalCandidate(
		CharacterBody3D body,
		float maxAbsY,
		in Vector3 preferredWallNormal,
		in InputCommand input,
		in Vector3 velocity)
	{
		Vector3 preferred = new(preferredWallNormal.X, 0.0f, preferredWallNormal.Z);
		bool hasPreferred = preferred.LengthSquared() > MinNormalLengthSq;
		if (hasPreferred)
		{
			preferred = preferred.Normalized();
		}

		Vector3 bestNormal = Vector3.Zero;
		float bestScore = float.NegativeInfinity;
		int collisionCount = body.GetSlideCollisionCount();
		for (int i = 0; i < collisionCount; i++)
		{
			KinematicCollision3D? collision = body.GetSlideCollision(i);
			if (collision is null)
			{
				continue;
			}

			Vector3 normal = collision.GetNormal();
			if (Mathf.Abs(normal.Y) >= maxAbsY)
			{
				continue;
			}

			Vector3 wallNormal = new(normal.X, 0.0f, normal.Z);
			float wallLengthSq = wallNormal.LengthSquared();
			if (wallLengthSq <= MinNormalLengthSq)
			{
				continue;
			}

			wallNormal = wallNormal.Normalized();
			float score = hasPreferred ? wallNormal.Dot(preferred) : wallLengthSq;
			if (score <= bestScore)
			{
				continue;
			}

			bestScore = score;
			bestNormal = wallNormal;
		}

		if (bestNormal != Vector3.Zero)
		{
			return bestNormal;
		}

		return ProbeWallNormalCandidate(body, maxAbsY, preferred, hasPreferred, input, velocity);
	}

	private static Vector3 ProbeWallNormalCandidate(
		CharacterBody3D body,
		float maxAbsY,
		in Vector3 preferredWallNormalXZ,
		bool hasPreferredWallNormal,
		in InputCommand input,
		in Vector3 velocity)
	{
		World3D? world = body.GetWorld3D();
		if (world is null)
		{
			return Vector3.Zero;
		}

		PhysicsDirectSpaceState3D space = world.DirectSpaceState;
		Vector3 forward = ComputeProbeForward(input, velocity);
		Vector3 right = Vector3.Up.Cross(forward);
		if (right.LengthSquared() <= MinNormalLengthSq)
		{
			right = Vector3.Right;
		}
		else
		{
			right = right.Normalized();
		}

		Vector3 baseOrigin = body.GlobalTransform.Origin + (Vector3.Up * WallProbeChestHeight);
		Vector3 bestNormal = Vector3.Zero;
		float bestScore = float.NegativeInfinity;

		if (TryProbeRay(body, space, baseOrigin + (-right * WallProbeSideOffset), -right, maxAbsY, out Vector3 leftNormal, out float leftDistance))
		{
			float score = hasPreferredWallNormal
				? leftNormal.Dot(preferredWallNormalXZ)
				: -leftDistance;
			if (score > bestScore)
			{
				bestScore = score;
				bestNormal = leftNormal;
			}
		}

		if (TryProbeRay(body, space, baseOrigin + (right * WallProbeSideOffset), right, maxAbsY, out Vector3 rightNormal, out float rightDistance))
		{
			float score = hasPreferredWallNormal
				? rightNormal.Dot(preferredWallNormalXZ)
				: -rightDistance;
			if (score > bestScore)
			{
				bestScore = score;
				bestNormal = rightNormal;
			}
		}

		return bestNormal;
	}

	private static bool TryProbeRay(
		CharacterBody3D body,
		PhysicsDirectSpaceState3D space,
		in Vector3 origin,
		in Vector3 direction,
		float maxAbsY,
		out Vector3 wallNormal,
		out float distance)
	{
		Vector3 dir = direction;
		if (dir.LengthSquared() <= MinNormalLengthSq)
		{
			wallNormal = Vector3.Zero;
			distance = 0.0f;
			return false;
		}

		dir = dir.Normalized();
		Vector3 target = origin + (dir * WallProbeDistance);
		PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, target, body.CollisionMask);
		query.CollideWithAreas = false;
		query.CollideWithBodies = true;
		query.Exclude = new Godot.Collections.Array<Rid> { body.GetRid() };

		Godot.Collections.Dictionary hit = space.IntersectRay(query);
		if (hit.Count == 0 || !hit.ContainsKey("normal") || !hit.ContainsKey("position"))
		{
			wallNormal = Vector3.Zero;
			distance = 0.0f;
			return false;
		}

		Vector3 normal = (Vector3)hit["normal"];
		if (Mathf.Abs(normal.Y) >= maxAbsY)
		{
			wallNormal = Vector3.Zero;
			distance = 0.0f;
			return false;
		}

		Vector3 projected = new(normal.X, 0.0f, normal.Z);
		if (projected.LengthSquared() <= MinNormalLengthSq)
		{
			wallNormal = Vector3.Zero;
			distance = 0.0f;
			return false;
		}

		Vector3 hitPos = (Vector3)hit["position"];
		distance = origin.DistanceTo(hitPos);
		wallNormal = projected.Normalized();
		return true;
	}

	private static Vector3 ComputeProbeForward(in InputCommand input, in Vector3 velocity)
	{
		Vector2 move = input.MoveAxes;
		if (move.LengthSquared() > 1.0f)
		{
			move = move.Normalized();
		}

		Vector3 localWish = new(move.X, 0.0f, -move.Y);
		if (localWish.LengthSquared() > MinNormalLengthSq)
		{
			return (new Basis(Vector3.Up, input.Yaw) * localWish).Normalized();
		}

		Vector3 horizontalVelocity = new(velocity.X, 0.0f, velocity.Z);
		if (horizontalVelocity.LengthSquared() > MinNormalLengthSq)
		{
			return horizontalVelocity.Normalized();
		}

		return new Vector3(-Mathf.Sin(input.Yaw), 0.0f, -Mathf.Cos(input.Yaw));
	}

	private static bool IsWallRunIntentActive(in InputCommand input)
	{
		return input.MoveAxes.LengthSquared() >= WallRunIntentMinMoveAxesSq;
	}

	private static LocomotionMode ResolveActiveMode(LocomotionMode mode, bool wasGrounded)
	{
		if (!EnableWallRun && mode == LocomotionMode.WallRun)
		{
			return wasGrounded ? LocomotionMode.Grounded : LocomotionMode.Air;
		}

		if (!EnableWallCling && mode == LocomotionMode.WallCling)
		{
			return wasGrounded ? LocomotionMode.Grounded : LocomotionMode.Air;
		}

		if (!EnableSlide && mode == LocomotionMode.Slide)
		{
			return wasGrounded ? LocomotionMode.Grounded : LocomotionMode.Air;
		}

		if (mode == LocomotionMode.Grounded && !wasGrounded)
		{
			return LocomotionMode.Air;
		}

		if (mode == LocomotionMode.Air && wasGrounded)
		{
			return LocomotionMode.Grounded;
		}

		return mode;
	}

	private static void GroundedStep(
		PlayerCharacter player,
		in InputCommand input,
		NetworkConfig config,
		ref Vector3 velocity,
		bool jumpPressed,
		bool wasGrounded)
	{
		ApplyHorizontal(input, config.MoveSpeed, config.GroundAcceleration, ref velocity);
		if (wasGrounded && jumpPressed && player.CanJump)
		{
			velocity.Y = config.JumpVelocity;
			player.OnJump();
			return;
		}

		velocity.Y = Mathf.Min(config.GroundStickVelocity, -0.01f);
	}

	private static void AirStep(in InputCommand input, NetworkConfig config, ref Vector3 velocity, bool wasGrounded)
	{
		float accel = wasGrounded ? config.GroundAcceleration : (config.AirAcceleration * config.AirControlFactor);
		ApplyHorizontal(input, config.MoveSpeed, accel, ref velocity);
		if (wasGrounded)
		{
			velocity.Y = Mathf.Min(config.GroundStickVelocity, -0.01f);
		}
		else
		{
			velocity.Y -= config.Gravity * input.DtFixed;
		}
	}

	private static WallRunStepResult WallRunStep(
		PlayerCharacter player,
		in InputCommand input,
		NetworkConfig config,
		ref Vector3 velocity,
		in LocomotionState state,
		bool jumpPressed)
	{
		if (!EnableWallRun)
		{
			AirStep(input, config, ref velocity, wasGrounded: false);
			return WallRunStepResult.None;
		}

		Vector3 wallNormal = new(state.WallNormal.X, 0.0f, state.WallNormal.Z);
		if (wallNormal.LengthSquared() <= MinNormalLengthSq)
		{
			AirStep(input, config, ref velocity, wasGrounded: false);
			return new WallRunStepResult(forceExitWallRun: true, didWallJump: false, cooldownTicks: 0);
		}

		wallNormal = wallNormal.Normalized();
		if (jumpPressed)
		{
			Vector3 tangentVelocity = velocity.Slide(wallNormal);
			velocity = tangentVelocity + (Vector3.Up * config.WallJumpUpVelocity) + (wallNormal * config.WallJumpAwayVelocity);
			player.OnJump();
			return new WallRunStepResult(
				forceExitWallRun: true,
				didWallJump: true,
				cooldownTicks: WallRunReattachCooldownTicks);
		}

		float normalSpeed = velocity.Dot(wallNormal);
		if (normalSpeed > 0.0f)
		{
			velocity -= wallNormal * normalSpeed;
			normalSpeed = 0.0f;
		}

		if (normalSpeed > -WallRunStickSpeed)
		{
			velocity += wallNormal * (-WallRunStickSpeed - normalSpeed);
		}

		ApplyHorizontalConstrainedToWall(input, config.MoveSpeed, config.GroundAcceleration, wallNormal, ref velocity);
		velocity.Y -= (config.Gravity * config.WallRunGravityScale) * input.DtFixed;
		return WallRunStepResult.None;
	}

	private static void WallClingStep(in InputCommand input, NetworkConfig config, ref Vector3 velocity, bool wasGrounded)
	{
		if (!EnableWallCling)
		{
			AirStep(input, config, ref velocity, wasGrounded);
			return;
		}

		// TODO(parkour): replace with cling-specific gravity and detach rules.
		AirStep(input, config, ref velocity, wasGrounded);
	}

	private static void SlideStep(in InputCommand input, NetworkConfig config, ref Vector3 velocity, bool wasGrounded)
	{
		if (!EnableSlide)
		{
			GroundedStepNoJump(input, config, ref velocity, wasGrounded);
			return;
		}

		// TODO(parkour): replace with slide friction/steering behavior.
		GroundedStepNoJump(input, config, ref velocity, wasGrounded);
	}

	private static void GroundedStepNoJump(in InputCommand input, NetworkConfig config, ref Vector3 velocity, bool wasGrounded)
	{
		float accel = wasGrounded ? config.GroundAcceleration : (config.AirAcceleration * config.AirControlFactor);
		ApplyHorizontal(input, config.MoveSpeed, accel, ref velocity);
		if (wasGrounded)
		{
			velocity.Y = Mathf.Min(config.GroundStickVelocity, -0.01f);
		}
		else
		{
			velocity.Y -= config.Gravity * input.DtFixed;
		}
	}

	private static void ApplyHorizontal(in InputCommand input, float moveSpeed, float acceleration, ref Vector3 velocity)
	{
		Vector2 move = input.MoveAxes;
		if (move.LengthSquared() > 1.0f)
		{
			move = move.Normalized();
		}

		Vector3 localWish = new(move.X, 0.0f, -move.Y);
		Vector3 worldWish = new Basis(Vector3.Up, input.Yaw) * localWish;
		Vector3 desiredHorizontal = worldWish * moveSpeed;
		Vector3 horizontalVelocity = new(velocity.X, 0.0f, velocity.Z);
		horizontalVelocity = horizontalVelocity.MoveToward(desiredHorizontal, acceleration * input.DtFixed);
		velocity.X = horizontalVelocity.X;
		velocity.Z = horizontalVelocity.Z;
	}

	private static void ApplyHorizontalConstrainedToWall(
		in InputCommand input,
		float moveSpeed,
		float acceleration,
		in Vector3 wallNormal,
		ref Vector3 velocity)
	{
		Vector2 move = input.MoveAxes;
		if (move.LengthSquared() > 1.0f)
		{
			move = move.Normalized();
		}

		Vector3 localWish = new(move.X, 0.0f, -move.Y);
		Vector3 worldWish = new Basis(Vector3.Up, input.Yaw) * localWish;
		Vector3 desiredHorizontal = worldWish.Slide(wallNormal);
		if (desiredHorizontal.LengthSquared() > MinNormalLengthSq)
		{
			desiredHorizontal = desiredHorizontal.Normalized() * moveSpeed;
		}
		else
		{
			desiredHorizontal = Vector3.Zero;
		}

		Vector3 horizontalVelocity = new Vector3(velocity.X, 0.0f, velocity.Z).Slide(wallNormal);
		horizontalVelocity = horizontalVelocity.MoveToward(desiredHorizontal, acceleration * input.DtFixed);
		velocity.X = horizontalVelocity.X;
		velocity.Z = horizontalVelocity.Z;
	}

	private static bool HasSupportAhead(
		CharacterBody3D body,
		float dtFixed,
		float floorSnapLength,
		bool wasGrounded,
		in Vector3 velocity)
	{
		if (!wasGrounded || floorSnapLength <= 0.0f)
		{
			return true;
		}

		Vector3 horizontalAdvance = new(velocity.X * dtFixed, 0.0f, velocity.Z * dtFixed);
		Transform3D supportProbeFrom = body.GlobalTransform.Translated(horizontalAdvance);
		Vector3 supportProbeMotion = Vector3.Down * (floorSnapLength + LedgeSupportProbeExtra);
		return body.TestMove(supportProbeFrom, supportProbeMotion);
	}

	private static void UpdateModeAndCounters(ref LocomotionState state, LocomotionMode nextMode)
	{
		if (state.Mode == nextMode)
		{
			state.ModeTicks++;
		}
		else
		{
			state.Mode = nextMode;
			state.ModeTicks = 0;
		}

		if (state.Mode != LocomotionMode.WallRun && state.Mode != LocomotionMode.WallCling)
		{
			state.WallNormal = Vector3.Zero;
		}

		if (state.Mode != LocomotionMode.Slide)
		{
			state.SlideTicksRemaining = 0;
		}
	}
}
