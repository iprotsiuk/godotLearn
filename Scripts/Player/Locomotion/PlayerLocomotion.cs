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
	private const float LedgeSupportProbeExtra = 0.05f;
	private const float WallContactMaxAbsY = 0.2f;
	private const float WallRunIntentMinMoveAxesSq = 0.01f;
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

		switch (activeMode)
		{
			case LocomotionMode.Grounded:
				GroundedStep(player, input, config, ref velocity, jumpPressed, wasGrounded);
				break;
			case LocomotionMode.Air:
				AirStep(input, config, ref velocity, wasGrounded);
				break;
			case LocomotionMode.WallRun:
				WallRunStep(player, input, config, ref velocity, state, jumpPressed, wasGrounded);
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
		Vector3 wallNormalCandidate = ComputeWallNormalCandidate(body, WallContactMaxAbsY);
		bool hasWallContact = wallNormalCandidate != Vector3.Zero;
		LocomotionMode nextMode;
		if (groundedAfter)
		{
			nextMode = LocomotionMode.Grounded;
			state.WallNormal = Vector3.Zero;
			state.WallRunTicksRemaining = 0;
		}
		else if (state.Mode == LocomotionMode.WallRun && hasWallContact && state.WallRunTicksRemaining > 0)
		{
			nextMode = LocomotionMode.WallRun;
			state.WallNormal = wallNormalCandidate;
			state.WallRunTicksRemaining = Mathf.Max(0, state.WallRunTicksRemaining - 1);
		}
		else if (state.Mode == LocomotionMode.Air &&
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
			state.WallRunTicksRemaining = 0;
		}
		UpdateModeAndCounters(ref state, nextMode);
		player.SetLocomotionState(state);
		player.PostSimUpdate();

		// TODO(parkour): compute wall sensors here (raycasts/slide-collision normals) and store in state.
		// TODO(parkour): when enabled, WallRun should modify gravity and constrain horizontal motion along wall tangent.
		// TODO(parkour): on wall-jump input, inject exit impulse and transition to Air.
		return new LocomotionStepResult(wasGrounded, groundedAfter, body.FloorSnapLength, state.Mode);
	}

	private static Vector3 ComputeWallNormalCandidate(CharacterBody3D body, float maxAbsY)
	{
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
			if (wallNormal.LengthSquared() <= 0.000001f)
			{
				continue;
			}

			return wallNormal.Normalized();
		}

		return Vector3.Zero;
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

	private static void WallRunStep(
		PlayerCharacter player,
		in InputCommand input,
		NetworkConfig config,
		ref Vector3 velocity,
		in LocomotionState state,
		bool jumpPressed,
		bool wasGrounded)
	{
		if (!EnableWallRun)
		{
			AirStep(input, config, ref velocity, wasGrounded);
			return;
		}

		// TODO(parkour): replace with actual wallrun dynamics and state transitions.
		AirStep(input, config, ref velocity, wasGrounded);
		if (jumpPressed && player.CanJump)
		{
			_ = state.WallNormal;
		}
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
			state.WallRunTicksRemaining = 0;
		}

		if (state.Mode != LocomotionMode.Slide)
		{
			state.SlideTicksRemaining = 0;
		}
	}
}
