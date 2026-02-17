using Godot;
using NetRunnerSlice.Player.Locomotion;

namespace NetRunnerSlice.Player.View;

public partial class PlayerCameraEffects : Node3D
{
	[Export] public float HeadbobAmplitudeY = 0.035f;
	[Export] public float HeadbobAmplitudeX = 0.012f;
	[Export] public float HeadbobFrequencyHz = 1.8f;
	[Export] public float HeadbobMinSpeed = 1.0f;
	[Export] public float HeadbobMaxSpeed = 9.0f;
	[Export] public float HeadbobPositionSmoothing = 14.0f;

	[Export] public float WallRunRollDegrees = 10.0f;
	[Export] public float RollSmoothing = 10.0f;
	[Export] public float WallRunMinSpeed = 2.0f;

	private PlayerCharacter? _ownerCharacter;
	private Camera3D? _camera;
	private Vector3 _basePosition;
	private Vector3 _baseRotation;
	private float _headbobPhase;
	private float _currentRollRad;

	public override void _Ready()
	{
		_basePosition = Position;
		_baseRotation = Rotation;
		_ownerCharacter = FindOwnerCharacter();
		_camera = GetNodeOrNull<Camera3D>("Camera3D");
	}

	public override void _Process(double delta)
	{
		float dt = Mathf.Max(0.0f, (float)delta);
		if (_ownerCharacter is null || _camera is null)
		{
			return;
		}

		bool isLocalView = _camera.Current || _ownerCharacter.LocalCamera is not null;
		if (!isLocalView)
		{
			return;
		}

		Vector3 ownerVelocity = _ownerCharacter.Velocity;
		Vector2 horizontalVelocity = new(ownerVelocity.X, ownerVelocity.Z);
		float speed = horizontalVelocity.Length();
		bool isGrounded = _ownerCharacter.Grounded;

		Vector3 targetOffset = Vector3.Zero;
		if (isGrounded && speed >= HeadbobMinSpeed)
		{
			float speedT = Mathf.InverseLerp(HeadbobMinSpeed, HeadbobMaxSpeed, speed);
			_headbobPhase += dt * Mathf.Tau * HeadbobFrequencyHz * Mathf.Lerp(0.5f, 1.4f, speedT);
			float bobY = Mathf.Sin(_headbobPhase) * HeadbobAmplitudeY * speedT;
			float bobX = Mathf.Cos(_headbobPhase * 0.5f) * HeadbobAmplitudeX * speedT;
			targetOffset = new Vector3(bobX, bobY, 0.0f);
		}

		float bobAlpha = 1.0f - Mathf.Exp(-Mathf.Max(0.01f, HeadbobPositionSmoothing) * dt);
		Position = Position.Lerp(_basePosition + targetOffset, bobAlpha);

		float targetRollRad = 0.0f;
		if (_ownerCharacter.CurrentLocomotionMode == LocomotionMode.WallRun && speed >= WallRunMinSpeed)
		{
			Vector3 wallNormal = _ownerCharacter.CurrentWallNormal;
			Vector3 wallNormalXZ = new(wallNormal.X, 0.0f, wallNormal.Z);
			if (wallNormalXZ.LengthSquared() > 0.0001f)
			{
				wallNormalXZ = wallNormalXZ.Normalized();
				Basis basis = _ownerCharacter.GlobalTransform.Basis;
				Vector3 localWallNormal = basis.Inverse() * wallNormalXZ;
				float baseTiltSide = Mathf.Sign(localWallNormal.X); // Away from wall for forward motion.
				if (Mathf.Abs(baseTiltSide) > 0.0f)
				{
					Vector3 runDir = new Vector3(ownerVelocity.X, 0.0f, ownerVelocity.Z).Slide(wallNormalXZ);
					float runFlip = 1.0f;
					if (runDir.LengthSquared() > 0.0001f)
					{
						Vector3 forward = -basis.Z;
						forward.Y = 0.0f;
						if (forward.LengthSquared() > 0.0001f)
						{
							float alongLook = runDir.Normalized().Dot(forward.Normalized());
							runFlip = alongLook < 0.0f ? -1.0f : 1.0f;
						}
					}

					targetRollRad = Mathf.DegToRad(WallRunRollDegrees) * -(baseTiltSide * runFlip);
				}
			}
		}

		float rollAlpha = 1.0f - Mathf.Exp(-Mathf.Max(0.01f, RollSmoothing) * dt);
		_currentRollRad = Mathf.Lerp(_currentRollRad, targetRollRad, rollAlpha);
		Rotation = new Vector3(_baseRotation.X, _baseRotation.Y, _baseRotation.Z + _currentRollRad);
	}

	private PlayerCharacter? FindOwnerCharacter()
	{
		Node? cursor = GetParent();
		while (cursor is not null)
		{
			if (cursor is PlayerCharacter playerCharacter)
			{
				return playerCharacter;
			}

			cursor = cursor.GetParent();
		}

		return null;
	}
}
