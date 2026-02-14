// Scripts/Player/PlayerMotor.cs
using Godot;
using NetRunnerSlice.Net;

namespace NetRunnerSlice.Player;

public static class PlayerMotor
{
	public static void Simulate(CharacterBody3D body, in InputCommand input, NetworkConfig config)
	{
		Vector3 velocity = body.Velocity;
		bool wasGrounded = body.IsOnFloor();

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

		body.Velocity = velocity;
		body.MoveAndSlide();
	}
}
