// Scripts/Player/PlayerMotor.cs
using System.Collections.Generic;
using Godot;
using NetRunnerSlice.Net;
using NetRunnerSlice.Player.Locomotion;

namespace NetRunnerSlice.Player;

public static class PlayerMotor
{
	private readonly struct FloorSnapDiagState
	{
		public readonly bool WasGrounded;
		public readonly bool IsOnFloor;
		public readonly float FloorSnapLength;
		public readonly LocomotionMode Mode;

		public FloorSnapDiagState(bool wasGrounded, bool isOnFloor, float floorSnapLength, LocomotionMode mode)
		{
			WasGrounded = wasGrounded;
			IsOnFloor = isOnFloor;
			FloorSnapLength = floorSnapLength;
			Mode = mode;
		}
	}

	private static readonly Dictionary<ulong, FloorSnapDiagState> s_floorSnapDiagByBody = new();

	public static bool LogFloorSnapDiagnostics { get; set; } = false;

	public static void Simulate(CharacterBody3D body, in InputCommand input, NetworkConfig config)
	{
		if (body is not PlayerCharacter playerCharacter)
		{
			SimulateWithoutLocomotionState(body, input, config);
			return;
		}

		LocomotionStepResult step = PlayerLocomotion.Step(playerCharacter, body, input, config);
		LogFloorSnapIfChanged(body, input.InputTick, step);
	}

	private static void LogFloorSnapIfChanged(CharacterBody3D body, uint inputTick, in LocomotionStepResult step)
	{
		if (!LogFloorSnapDiagnostics)
		{
			return;
		}

		ulong bodyId = body.GetInstanceId();
		FloorSnapDiagState next = new(
			step.WasGrounded,
			step.IsOnFloor,
			step.FloorSnapLength,
			step.Mode);
		if (s_floorSnapDiagByBody.TryGetValue(bodyId, out FloorSnapDiagState previous)
			&& previous.WasGrounded == next.WasGrounded
			&& previous.IsOnFloor == next.IsOnFloor
			&& Mathf.IsEqualApprox(previous.FloorSnapLength, next.FloorSnapLength)
			&& previous.Mode == next.Mode)
		{
			return;
		}

		s_floorSnapDiagByBody[bodyId] = next;
		GD.Print(
			$"FloorSnapDiag: body={bodyId} tick={inputTick} wasGrounded={next.WasGrounded} " +
			$"isOnFloor={next.IsOnFloor} floorSnapLength={next.FloorSnapLength:0.###} mode={next.Mode}");
	}

	private static void SimulateWithoutLocomotionState(CharacterBody3D body, in InputCommand input, NetworkConfig config)
	{
		Vector3 velocity = body.Velocity;
		bool wasGrounded = body.IsOnFloor();
		Vector2 move = input.MoveAxes;
		if (move.LengthSquared() > 1.0f)
		{
			move = move.Normalized();
		}

		Vector3 localWish = new(move.X, 0.0f, -move.Y);
		Vector3 worldWish = new Basis(Vector3.Up, input.Yaw) * localWish;
		Vector3 desiredHorizontal = worldWish * config.MoveSpeed;
		float accel = wasGrounded ? config.GroundAcceleration : (config.AirAcceleration * config.AirControlFactor);
		Vector3 horizontalVelocity = new(velocity.X, 0.0f, velocity.Z);
		horizontalVelocity = horizontalVelocity.MoveToward(desiredHorizontal, accel * input.DtFixed);
		velocity.X = horizontalVelocity.X;
		velocity.Z = horizontalVelocity.Z;

		bool jumpPressed = (input.Buttons & InputButtons.JumpPressed) != 0;
		if (wasGrounded)
		{
			if (jumpPressed)
			{
				velocity.Y = config.JumpVelocity;
			}
			else
			{
				velocity.Y = Mathf.Min(config.GroundStickVelocity, -0.01f);
			}
		}
		else
		{
			velocity.Y -= config.Gravity * input.DtFixed;
		}

		body.FloorSnapLength = wasGrounded && !jumpPressed && velocity.Y <= 0.0f ? config.FloorSnapLength : 0.0f;
		body.Velocity = velocity;
		body.MoveAndSlide();
	}
}
