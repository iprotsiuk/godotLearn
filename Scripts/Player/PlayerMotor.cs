// Scripts/Player/PlayerMotor.cs
using System.Collections.Generic;
using Godot;
using NetRunnerSlice.Net;

namespace NetRunnerSlice.Player;

public static class PlayerMotor
{
	private readonly struct FloorSnapDiagState
	{
		public readonly bool WasGrounded;
		public readonly bool IsOnFloor;
		public readonly float FloorSnapLength;

		public FloorSnapDiagState(bool wasGrounded, bool isOnFloor, float floorSnapLength)
		{
			WasGrounded = wasGrounded;
			IsOnFloor = isOnFloor;
			FloorSnapLength = floorSnapLength;
		}
	}

	private static readonly Dictionary<ulong, FloorSnapDiagState> s_floorSnapDiagByBody = new();

	public static bool LogFloorSnapDiagnostics { get; set; } = false;

	public static void Simulate(CharacterBody3D body, in InputCommand input, NetworkConfig config)
	{
		Vector3 velocity = body.Velocity;
		bool wasGrounded = body.IsOnFloor();
		PlayerCharacter? playerCharacter = body as PlayerCharacter;
		if (playerCharacter is not null && playerCharacter.TryConsumeGroundedOverride(out bool groundedOverride))
		{
			wasGrounded = groundedOverride;
		}

		Vector2 move = input.MoveAxes;
		if (move.LengthSquared() > 1.0f)
		{
			move = move.Normalized();
		}

		Vector3 localWish = new Vector3(move.X, 0.0f, -move.Y);
		Vector3 worldWish = new Basis(Vector3.Up, input.Yaw) * localWish;
		Vector3 desiredHorizontal = worldWish * config.MoveSpeed;

		float accel = wasGrounded ? config.GroundAcceleration : (config.AirAcceleration * config.AirControlFactor);
		Vector3 horizontalVelocity = new Vector3(velocity.X, 0.0f, velocity.Z);
		horizontalVelocity = horizontalVelocity.MoveToward(desiredHorizontal, accel * input.DtFixed);
		velocity.X = horizontalVelocity.X;
		velocity.Z = horizontalVelocity.Z;

		bool jumpPressed = (input.Buttons & InputButtons.JumpPressed) != 0;
		if (wasGrounded)
		{
			if (jumpPressed && playerCharacter is not null && playerCharacter.CanJump)
			{
				velocity.Y = config.JumpVelocity;
				playerCharacter.OnJump();
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

		bool hasSupportAhead = true;
		if (wasGrounded && config.FloorSnapLength > 0.0f)
		{
			// Predict support under the next horizontal step to avoid one-tick ledge reattachment.
			Vector3 horizontalAdvance = new(velocity.X * input.DtFixed, 0.0f, velocity.Z * input.DtFixed);
			Transform3D supportProbeFrom = body.GlobalTransform.Translated(horizontalAdvance);
			Vector3 supportProbeMotion = Vector3.Down * (config.FloorSnapLength + 0.05f);
			hasSupportAhead = body.TestMove(supportProbeFrom, supportProbeMotion);
		}

		bool allowSnap = wasGrounded && !jumpPressed && velocity.Y <= 0.0f && hasSupportAhead;
		body.FloorSnapLength = allowSnap ? config.FloorSnapLength : 0.0f;

		body.Velocity = velocity;
		body.MoveAndSlide();

		LogFloorSnapIfChanged(body, input.InputTick, wasGrounded);
		playerCharacter?.PostSimUpdate();
	}

	private static void LogFloorSnapIfChanged(CharacterBody3D body, uint inputTick, bool wasGrounded)
	{
		if (!LogFloorSnapDiagnostics)
		{
			return;
		}

		ulong bodyId = body.GetInstanceId();
		FloorSnapDiagState next = new(
			wasGrounded,
			body.IsOnFloor(),
			body.FloorSnapLength);
		if (s_floorSnapDiagByBody.TryGetValue(bodyId, out FloorSnapDiagState previous)
			&& previous.WasGrounded == next.WasGrounded
			&& previous.IsOnFloor == next.IsOnFloor
			&& Mathf.IsEqualApprox(previous.FloorSnapLength, next.FloorSnapLength))
		{
			return;
		}

		s_floorSnapDiagByBody[bodyId] = next;
		GD.Print(
			$"FloorSnapDiag: body={bodyId} tick={inputTick} wasGrounded={next.WasGrounded} " +
			$"isOnFloor={next.IsOnFloor} floorSnapLength={next.FloorSnapLength:0.###}");
	}
}
