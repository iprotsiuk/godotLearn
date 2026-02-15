// Scripts/Player/PlayerCharacter.cs
using Godot;
using NetRunnerSlice.Net;

namespace NetRunnerSlice.Player;

public partial class PlayerCharacter : CharacterBody3D
{
	private const float MinSmoothSec = 0.01f;
	private const float MaxCorrectionStepPerFrame = 0.35f;
	private const float MaxQueuedCorrection = 2.0f;
	private const float MaxCorrectionSpeed = 100.0f;

	private readonly Node3D _visualRoot = new();
	private readonly Node3D _visualYawRoot = new();
	private readonly Node3D _visualPitchRoot = new();
	private readonly Node3D _cameraYawRoot = new();
	private readonly Node3D _cameraPitchRoot = new();

	private MeshInstance3D? _bodyMesh;
	private MeshInstance3D? _headMesh;
	private Camera3D? _camera;

	private Vector3 _renderOffset;
	private Vector3 _renderVelocity;
	private float _renderSmoothSec = 0.1f;
	private Vector3 _viewOffset;
	private Vector3 _viewVelocity;
	private float _viewSmoothSec = 0.1f;
	private bool _jumpLocked;
	private bool _hasLeftGroundSinceJump;
	private bool _groundedOverrideValid;
	private bool _groundedOverrideValue;

	private bool _initialized;

	public int PeerId { get; private set; }

	public float Yaw { get; private set; }

	public float Pitch { get; private set; }

	public bool Grounded => IsOnFloor();
	public bool CanJump => !_jumpLocked;

	public Camera3D? LocalCamera => _camera;

	public void Setup(int peerId, bool withCamera, Color tint)
	{
		if (_initialized)
		{
			return;
		}

		PeerId = peerId;
		CollisionLayer = 2;
		CollisionMask = 1;
		UpDirection = Vector3.Up;
		FloorStopOnSlope = true;
		FloorSnapLength = 0.25f;

		CollisionShape3D collision = new();
		CapsuleShape3D capsule = new()
		{
			Radius = 0.35f,
			Height = 1.1f
		};
		collision.Shape = capsule;
		collision.Position = new Vector3(0.0f, 0.9f, 0.0f);
		AddChild(collision);

		AddChild(_visualRoot);
		_visualRoot.AddChild(_visualYawRoot);
		_visualYawRoot.AddChild(_visualPitchRoot);
		_visualPitchRoot.Position = new Vector3(0.0f, 1.55f, 0.0f);

		AddChild(_cameraYawRoot);
		_cameraYawRoot.AddChild(_cameraPitchRoot);
		_cameraPitchRoot.Position = new Vector3(0.0f, 1.55f, 0.0f);

		_bodyMesh = new MeshInstance3D
		{
			Mesh = new CapsuleMesh
			{
				Radius = 0.35f,
				Height = 1.1f
			},
			Position = new Vector3(0.0f, 0.9f, 0.0f)
		};

		_headMesh = new MeshInstance3D
		{
			Mesh = new SphereMesh { Radius = 0.18f, Height = 0.36f },
			Position = new Vector3(0.0f, 0.0f, 0.0f)
		};

		StandardMaterial3D material = new()
		{
			AlbedoColor = tint,
			Roughness = 0.6f,
			Metallic = 0.0f
		};

		_bodyMesh.MaterialOverride = material;
		_headMesh.MaterialOverride = material;
		_visualYawRoot.AddChild(_bodyMesh);
		_visualPitchRoot.AddChild(_headMesh);

		if (withCamera)
		{
			_camera = new Camera3D
			{
				Current = true,
				Position = Vector3.Zero,
				Near = 0.05f,
				Far = 500.0f,
				Fov = 90.0f
			};

			_cameraPitchRoot.AddChild(_camera);
			_bodyMesh.Visible = false;
			_headMesh.Visible = false;
		}

		_initialized = true;
	}

	public override void _Process(double delta)
	{
		float dt = Mathf.Max(0.0f, (float)delta);

		if (_renderOffset.LengthSquared() <= 0.000001f && _renderVelocity.LengthSquared() <= 0.000001f)
		{
			_renderOffset = Vector3.Zero;
			_renderVelocity = Vector3.Zero;
			_visualRoot.Position = Vector3.Zero;
		}
		else
		{
			Vector3 next = SmoothDamp(
				_renderOffset,
				Vector3.Zero,
				ref _renderVelocity,
				Mathf.Max(MinSmoothSec, _renderSmoothSec),
				MaxCorrectionSpeed,
				dt);
			_renderOffset = ClampStep(_renderOffset, next, MaxCorrectionStepPerFrame);
			_visualRoot.Position = _renderOffset;
		}

		if (_camera is null)
		{
			return;
		}

		if (_viewOffset.LengthSquared() <= 0.000001f && _viewVelocity.LengthSquared() <= 0.000001f)
		{
			_viewOffset = Vector3.Zero;
			_viewVelocity = Vector3.Zero;
			_cameraYawRoot.Position = Vector3.Zero;
		}
		else
		{
			Vector3 next = SmoothDamp(
				_viewOffset,
				Vector3.Zero,
				ref _viewVelocity,
				Mathf.Max(MinSmoothSec, _viewSmoothSec),
				MaxCorrectionSpeed,
				dt);
			_viewOffset = ClampStep(_viewOffset, next, MaxCorrectionStepPerFrame);
			_cameraYawRoot.Position = _viewOffset;
		}
	}

	public void SetLook(float yaw, float pitch)
	{
		Yaw = yaw;
		Pitch = pitch;
		_visualYawRoot.Rotation = new Vector3(0.0f, yaw, 0.0f);
		_visualPitchRoot.Rotation = new Vector3(pitch, 0.0f, 0.0f);
		_cameraYawRoot.Rotation = new Vector3(0.0f, yaw, 0.0f);
		_cameraPitchRoot.Rotation = new Vector3(pitch, 0.0f, 0.0f);
	}

	public void SetFromSnapshot(in PlayerStateSnapshot snapshot)
	{
		GlobalPosition = snapshot.Pos;
		Velocity = snapshot.Vel;
		SetLook(snapshot.Yaw, snapshot.Pitch);
	}

	public void AddRenderCorrection(Vector3 offset, int smoothMs)
	{
		_renderOffset += offset;
		_renderOffset = ClampMagnitude(_renderOffset, MaxQueuedCorrection);
		_renderSmoothSec = Mathf.Max(MinSmoothSec, smoothMs / 1000.0f);
	}

	public void ClearRenderCorrection()
	{
		_renderOffset = Vector3.Zero;
		_renderVelocity = Vector3.Zero;
		_visualRoot.Position = Vector3.Zero;
	}

	public void AddViewCorrection(Vector3 offset, int smoothMs)
	{
		_viewOffset += offset;
		_viewOffset = ClampMagnitude(_viewOffset, MaxQueuedCorrection);
		_viewSmoothSec = Mathf.Max(MinSmoothSec, smoothMs / 1000.0f);
	}

	public void ClearViewCorrection()
	{
		_viewOffset = Vector3.Zero;
		_viewVelocity = Vector3.Zero;
		if (_camera is not null)
		{
			_cameraYawRoot.Position = Vector3.Zero;
		}
	}

	public void OnJump()
	{
		_jumpLocked = true;
		_hasLeftGroundSinceJump = false;
	}

	public void PostSimUpdate()
	{
		bool grounded = IsOnFloor();
		if (!grounded)
		{
			_hasLeftGroundSinceJump = true;
		}

		if (_jumpLocked && grounded && _hasLeftGroundSinceJump)
		{
			_jumpLocked = false;
			_hasLeftGroundSinceJump = false;
		}
	}

	public void SetGroundedOverride(bool grounded)
	{
		_groundedOverrideValid = true;
		_groundedOverrideValue = grounded;
	}

	public bool TryConsumeGroundedOverride(out bool grounded)
	{
		if (_groundedOverrideValid)
		{
			grounded = _groundedOverrideValue;
			_groundedOverrideValid = false;
			return true;
		}

		grounded = false;
		return false;
	}

	private static Vector3 ClampMagnitude(Vector3 value, float maxLength)
	{
		float lengthSq = value.LengthSquared();
		float maxSq = maxLength * maxLength;
		if (lengthSq <= maxSq || lengthSq <= 0.000001f)
		{
			return value;
		}

		return value.Normalized() * maxLength;
	}

	private static Vector3 ClampStep(Vector3 current, Vector3 next, float maxStep)
	{
		Vector3 delta = next - current;
		float length = delta.Length();
		if (length <= maxStep || length <= 0.000001f)
		{
			return next;
		}

		return current + (delta / length) * maxStep;
	}

	private static Vector3 SmoothDamp(
		Vector3 current,
		Vector3 target,
		ref Vector3 currentVelocity,
		float smoothTime,
		float maxSpeed,
		float deltaTime)
	{
		smoothTime = Mathf.Max(0.0001f, smoothTime);
		deltaTime = Mathf.Max(0.0001f, deltaTime);
		float omega = 2.0f / smoothTime;
		float x = omega * deltaTime;
		float exp = 1.0f / (1.0f + x + (0.48f * x * x) + (0.235f * x * x * x));

		Vector3 change = current - target;
		Vector3 originalTo = target;
		float maxChange = maxSpeed * smoothTime;
		change = ClampMagnitude(change, maxChange);
		target = current - change;

		Vector3 temp = (currentVelocity + (omega * change)) * deltaTime;
		currentVelocity = (currentVelocity - (omega * temp)) * exp;
		Vector3 output = target + ((change + temp) * exp);

		if ((originalTo - current).Dot(output - originalTo) > 0.0f)
		{
			output = originalTo;
			currentVelocity = Vector3.Zero;
		}

		return output;
	}
}
