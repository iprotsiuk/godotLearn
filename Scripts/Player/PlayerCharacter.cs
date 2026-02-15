// Scripts/Player/PlayerCharacter.cs
using Godot;
using NetRunnerSlice.Net;

namespace NetRunnerSlice.Player;

public partial class PlayerCharacter : CharacterBody3D
{
	private readonly Node3D _renderRoot = new();
	private readonly Node3D _yawRoot = new();
	private readonly Node3D _pitchRoot = new();

	private MeshInstance3D? _bodyMesh;
	private MeshInstance3D? _headMesh;
	private Camera3D? _camera;

	private Vector3 _renderOffset;
	private float _renderSmoothSec = 0.1f;
	private bool _jumpLocked;
	private bool _wasAirborne;

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

		AddChild(_renderRoot);
		_renderRoot.AddChild(_yawRoot);
		_yawRoot.AddChild(_pitchRoot);
		_pitchRoot.Position = new Vector3(0.0f, 1.55f, 0.0f);

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
		_yawRoot.AddChild(_bodyMesh);
		_pitchRoot.AddChild(_headMesh);

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

			_pitchRoot.AddChild(_camera);
			_bodyMesh.Visible = false;
			_headMesh.Visible = false;
		}

		_initialized = true;
	}

	public override void _Process(double delta)
	{
		if (_renderOffset.LengthSquared() <= 0.000001f)
		{
			_renderRoot.Position = Vector3.Zero;
			return;
		}

		float t = 1.0f - Mathf.Exp((float)(-delta / Mathf.Max(0.001f, _renderSmoothSec)));
		_renderOffset = _renderOffset.Lerp(Vector3.Zero, t);
		_renderRoot.Position = _renderOffset;
	}

	public void SetLook(float yaw, float pitch)
	{
		Yaw = yaw;
		Pitch = pitch;
		_yawRoot.Rotation = new Vector3(0.0f, yaw, 0.0f);
		_pitchRoot.Rotation = new Vector3(pitch, 0.0f, 0.0f);
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
		_renderSmoothSec = Mathf.Max(0.01f, smoothMs / 1000.0f);
	}

	public void ClearRenderCorrection()
	{
		_renderOffset = Vector3.Zero;
		_renderRoot.Position = Vector3.Zero;
	}

	public void OnJump()
	{
		_jumpLocked = true;
		_wasAirborne = true;
	}

	public void PostSimUpdate()
	{
		bool grounded = IsOnFloor();
		if (!grounded)
		{
			_wasAirborne = true;
		}

		if (_jumpLocked && _wasAirborne && grounded)
		{
			_jumpLocked = false;
			_wasAirborne = false;
		}
	}
}
