// Scripts/Player/PlayerCharacter.cs
using Godot;
using NetRunnerSlice.Net;
using NetRunnerSlice.Player.Locomotion;

namespace NetRunnerSlice.Player;

public partial class PlayerCharacter : CharacterBody3D
{
	private const string PlayerCharacterSceneName = "PlayerCharacter.tscn";
	private const string FirstPersonViewRigScenePath = "res://Scenes/Player/FirstPersonViewRig.tscn";
	private const string ThirdPersonModelScenePath = "res://Scenes/Player/ThirdPersonModel.tscn";
	private const float MinSmoothSec = 0.01f;
	private const float MaxQueuedCorrection = 2.0f;
	private const float MaxCorrectionSpeed = 100.0f;

	[Export] public NodePath VisualRootPath { get; set; } = new("VisualRoot");
	[Export] public NodePath VisualYawRootPath { get; set; } = new("VisualRoot/VisualYawRoot");
	[Export] public NodePath VisualPitchRootPath { get; set; } = new("VisualRoot/VisualYawRoot/VisualPitchRoot");
	[Export] public NodePath CameraYawRootPath { get; set; } = new("CameraYawRoot");
	[Export] public NodePath CameraPitchRootPath { get; set; } = new("CameraYawRoot/CameraPitchRoot");
	[Export] public NodePath CollisionPath { get; set; } = new("Collision");
	[Export] public string PreferredRunAnimation { get; set; } = "run";
	[Export] public string PreferredIdleAnimation { get; set; } = "idle";
	[Export(PropertyHint.Range, "0.0,3.0,0.01")] public float RunAnimationSpeedThreshold { get; set; } = 0.15f;
	[Export(PropertyHint.Range, "0.0,1.0,0.01")] public float AnimationBlendSeconds { get; set; } = 0.12f;

	private Node3D _visualRoot = null!;
	private Node3D _visualYawRoot = null!;
	private Node3D _visualPitchRoot = null!;
	private Node3D _cameraYawRoot = null!;
	private Node3D _cameraPitchRoot = null!;

	private Node3D? _firstPersonViewRigRoot;
	private Node3D? _thirdPersonModelRoot;
	private AnimationPlayer? _thirdPersonAnimator;
	private string _runAnimationName = string.Empty;
	private string _idleAnimationName = string.Empty;
	private Camera3D? _camera;
	private Color _tint;
	private bool _withCamera;
	private float _localCameraFov = 90.0f;

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
	private LocomotionState _locomotionState = LocomotionState.CreateInitial(grounded: true);

	private bool _initialized;
	private bool _bound;

	public int PeerId { get; private set; }

	public float Yaw { get; private set; }

	public float Pitch { get; private set; }

	public bool Grounded => IsOnFloor();
	public bool CanJump => !_jumpLocked;
	public LocomotionMode CurrentLocomotionMode => _locomotionState.Mode;
	public Vector3 CurrentWallNormal => _locomotionState.WallNormal;

	public Camera3D? LocalCamera => _camera;
	public Vector3 RenderCorrectionOffset => _renderOffset;
	public Vector3 ViewCorrectionOffset => _viewOffset;
	public Vector3 CameraCorrectionOffset => _camera is null ? Vector3.Zero : _cameraYawRoot.Position;

	public void Setup(int peerId, bool withCamera, Color tint, float localCameraFov = 90.0f)
	{
		EnsureBound();

		if (_initialized)
		{
			return;
		}

		PeerId = peerId;
		_withCamera = withCamera;
		_tint = tint;
		_localCameraFov = localCameraFov;
		CollisionLayer = 2;
		CollisionMask = 1;
		UpDirection = Vector3.Up;
		FloorStopOnSlope = true;
		FloorSnapLength = 0.0f;

		const int OFF = 2;
		_visualRoot.Set("physics_interpolation_mode", OFF);
		_visualYawRoot.Set("physics_interpolation_mode", OFF);
		_visualPitchRoot.Set("physics_interpolation_mode", OFF);
		_cameraYawRoot.Set("physics_interpolation_mode", OFF);
		_cameraPitchRoot.Set("physics_interpolation_mode", OFF);

		if (withCamera)
		{
			EnsureFirstPersonViewRig();
		}

		_initialized = true;
	}

	public override void _Ready()
	{
		EnsureThirdPersonModel();
	}

	public override void _Process(double delta)
	{
		EnsureBound();

		float dt = Mathf.Max(0.0f, (float)delta);
		UpdateThirdPersonAnimation();

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
			_renderOffset = next;
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
			_viewOffset = next;
		}

		// Keep local camera aligned with the same smoothed XZ render correction as the visual body.
		_cameraYawRoot.Position = _renderOffset + _viewOffset;
	}

	public void SetLook(float yaw, float pitch)
	{
		EnsureBound();

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
		offset.X = 0.0f;
		offset.Z = 0.0f;
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
			_cameraYawRoot.Position = _renderOffset;
		}
	}

	public void ResetInterpolationAfterSnap()
	{
		ResetPhysicsInterpolation();
		_visualRoot.ResetPhysicsInterpolation();
		_visualYawRoot.ResetPhysicsInterpolation();
		_visualPitchRoot.ResetPhysicsInterpolation();
		_cameraYawRoot.ResetPhysicsInterpolation();
		_cameraPitchRoot.ResetPhysicsInterpolation();
		_camera?.ResetPhysicsInterpolation();
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

	public void ResetLocomotionFromAuthoritative(bool grounded)
	{
		_locomotionState = LocomotionState.CreateInitial(grounded);
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

	internal LocomotionState GetLocomotionState()
	{
		return _locomotionState;
	}

	internal void SetLocomotionState(in LocomotionState state)
	{
		_locomotionState = state;
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

	private void EnsureThirdPersonModel()
	{
		if (_thirdPersonModelRoot is not null || !Visible)
		{
			return;
		}

		PackedScene? modelScene = ResourceLoader.Load<PackedScene>(ThirdPersonModelScenePath);
		if (modelScene is null)
		{
			GD.PushWarning($"Failed to load third-person model scene: {ThirdPersonModelScenePath}");
			return;
		}

		Node? instance = modelScene.Instantiate();
		if (instance is not Node3D modelRoot)
		{
			GD.PushWarning($"Third-person model root must be Node3D: {ThirdPersonModelScenePath}");
			instance.QueueFree();
			return;
		}

		ApplyTintRecursive(modelRoot, _tint);
		modelRoot.Visible = !_withCamera;
		_visualYawRoot.AddChild(modelRoot);
		_thirdPersonModelRoot = modelRoot;
		BindThirdPersonAnimation(modelRoot);
	}

	private void EnsureFirstPersonViewRig()
	{
		if (_firstPersonViewRigRoot is not null || !_withCamera)
		{
			return;
		}

		PackedScene? viewRigScene = ResourceLoader.Load<PackedScene>(FirstPersonViewRigScenePath);
		if (viewRigScene is null)
		{
			GD.PushWarning($"Failed to load first-person view rig scene: {FirstPersonViewRigScenePath}");
			return;
		}

		Node? instance = viewRigScene.Instantiate();
		if (instance is not Node3D viewRigRoot)
		{
			GD.PushWarning($"First-person view rig root must be Node3D: {FirstPersonViewRigScenePath}");
			instance.QueueFree();
			return;
		}

		_cameraPitchRoot.AddChild(viewRigRoot);
		_firstPersonViewRigRoot = viewRigRoot;

		Node3D? effects = viewRigRoot.GetNodeOrNull<Node3D>("Effects");
		effects?.Set("physics_interpolation_mode", 2);
		effects?.GetNodeOrNull<Node3D>("ArmsRoot")?.Set("physics_interpolation_mode", 2);

		Camera3D? camera = viewRigRoot.GetNodeOrNull<Camera3D>("Effects/Camera3D");
		if (camera is null)
		{
			GD.PushWarning($"First-person view rig is missing Effects/Camera3D: {FirstPersonViewRigScenePath}");
			return;
		}

		camera.Current = true;
		camera.Fov = _localCameraFov;
		_camera = camera;
		if (_thirdPersonModelRoot is not null)
		{
			_thirdPersonModelRoot.Visible = false;
		}
	}

	private static void ApplyTintRecursive(Node node, Color tint)
	{
		if (node is GeometryInstance3D geometry)
		{
			if (geometry.MaterialOverride is BaseMaterial3D overrideMaterial)
			{
				BaseMaterial3D cloned = (BaseMaterial3D)overrideMaterial.Duplicate();
				cloned.AlbedoColor *= tint;
				geometry.MaterialOverride = cloned;
			}
			else if (geometry is MeshInstance3D meshInstance && meshInstance.Mesh is Mesh mesh)
			{
				bool appliedFromSurface = false;
				int surfaceCount = mesh.GetSurfaceCount();
				for (int surface = 0; surface < surfaceCount; surface++)
				{
					Material? source = meshInstance.GetSurfaceOverrideMaterial(surface) ?? mesh.SurfaceGetMaterial(surface);
					if (source is not BaseMaterial3D baseMaterial)
					{
						continue;
					}

					BaseMaterial3D cloned = (BaseMaterial3D)baseMaterial.Duplicate();
					cloned.AlbedoColor *= tint;
					meshInstance.SetSurfaceOverrideMaterial(surface, cloned);
					appliedFromSurface = true;
				}

				if (!appliedFromSurface)
				{
					geometry.MaterialOverride = new StandardMaterial3D
					{
						AlbedoColor = tint,
						Roughness = 0.6f,
						Metallic = 0.0f
					};
				}
			}
			else
			{
				geometry.MaterialOverride = new StandardMaterial3D
				{
					AlbedoColor = tint,
					Roughness = 0.6f,
					Metallic = 0.0f
				};
			}
		}

		foreach (Node child in node.GetChildren())
		{
			ApplyTintRecursive(child, tint);
		}
	}

	private void UpdateThirdPersonAnimation()
	{
		if (_thirdPersonAnimator is null)
		{
			return;
		}

		float horizontalSpeed = new Vector2(Velocity.X, Velocity.Z).Length();
		bool shouldRun = horizontalSpeed >= Mathf.Max(0.0f, RunAnimationSpeedThreshold);

		if (shouldRun)
		{
			if (!string.IsNullOrEmpty(_runAnimationName))
			{
				bool isCurrentRun = _thirdPersonAnimator.CurrentAnimation.ToString().Equals(_runAnimationName, System.StringComparison.Ordinal);
				if (!isCurrentRun)
				{
					_thirdPersonAnimator.Play(_runAnimationName, Mathf.Max(0.0f, AnimationBlendSeconds));
				}
				else if (!_thirdPersonAnimator.IsPlaying())
				{
					// Imported clips can be non-looping/read-only; restart while movement continues.
					_thirdPersonAnimator.Play(_runAnimationName, 0.0f);
				}
			}
			return;
		}

		if (!string.IsNullOrEmpty(_idleAnimationName))
		{
			if (!_thirdPersonAnimator.CurrentAnimation.ToString().Equals(_idleAnimationName, System.StringComparison.Ordinal))
			{
				_thirdPersonAnimator.Play(_idleAnimationName, Mathf.Max(0.0f, AnimationBlendSeconds));
			}
			return;
		}

		if (_thirdPersonAnimator.IsPlaying())
		{
			_thirdPersonAnimator.Stop();
			_thirdPersonAnimator.Seek(0.0, true);
		}
	}

	private void BindThirdPersonAnimation(Node modelRoot)
	{
		_thirdPersonAnimator = FindAnimationPlayer(modelRoot);
		if (_thirdPersonAnimator is null)
		{
			GD.PushWarning("Third-person model has no AnimationPlayer. Run/idle animation switching is disabled.");
			return;
		}

		_runAnimationName = ResolveAnimationName(_thirdPersonAnimator, PreferredRunAnimation, "run", "sprint", "jog", "walk");
		_idleAnimationName = ResolveAnimationName(_thirdPersonAnimator, PreferredIdleAnimation, "idle", "rest", "stand");

		if (string.IsNullOrEmpty(_runAnimationName))
		{
			string[] available = _thirdPersonAnimator.GetAnimationList();
			if (available.Length > 0)
			{
				_runAnimationName = available[0];
				GD.PushWarning($"Could not resolve run animation. Falling back to first clip: {_runAnimationName}");
			}
			else
			{
				GD.PushWarning("Could not resolve a run animation on third-person model. Set PreferredRunAnimation on PlayerCharacter.");
			}
		}
		else
		{
			_runAnimationName = EnsureLoopingAnimation(_thirdPersonAnimator, _runAnimationName);
		}

		if (string.IsNullOrEmpty(_idleAnimationName))
		{
			GD.PushWarning("Could not resolve an idle animation on third-person model. Set PreferredIdleAnimation on PlayerCharacter.");
		}
		else
		{
			_thirdPersonAnimator.Play(_idleAnimationName);
		}
	}

	private static AnimationPlayer? FindAnimationPlayer(Node root)
	{
		if (root is AnimationPlayer animationPlayer)
		{
			return animationPlayer;
		}

		foreach (Node child in root.GetChildren())
		{
			AnimationPlayer? found = FindAnimationPlayer(child);
			if (found is not null)
			{
				return found;
			}
		}

		return null;
	}

	private static string ResolveAnimationName(AnimationPlayer player, string preferredName, params string[] fallbackKeywords)
	{
		if (!string.IsNullOrWhiteSpace(preferredName))
		{
			StringName preferred = new(preferredName);
			if (player.HasAnimation(preferred))
			{
				return preferredName;
			}
		}

		string[] animations = player.GetAnimationList();
		foreach (string name in animations)
		{
			foreach (string keyword in fallbackKeywords)
			{
				if (name.Contains(keyword, System.StringComparison.OrdinalIgnoreCase))
				{
					return name;
				}
			}
		}

		return string.Empty;
	}

	private static string EnsureLoopingAnimation(AnimationPlayer player, string sourceAnimationName)
	{
		StringName sourceName = new(sourceAnimationName);
		Animation? source = player.GetAnimation(sourceName);
		if (source is null)
		{
			return sourceAnimationName;
		}

		if (source.LoopMode == Animation.LoopModeEnum.Linear)
		{
			return sourceAnimationName;
		}

		source.LoopMode = Animation.LoopModeEnum.Linear;
		return sourceAnimationName;
	}

	private T RequireNode<T>(NodePath path, string pathName) where T : Node
	{
		T? node = GetNodeOrNull<T>(path);
		if (node is not null)
		{
			return node;
		}

		string message = $"PlayerCharacter is missing required node at path '{path}' for {pathName} (expected type {typeof(T).Name}) in scene {PlayerCharacterSceneName}.";
		GD.PushError(message);
		throw new System.InvalidOperationException(message);
	}

	private void EnsureBound()
	{
		if (_bound)
		{
			return;
		}

		RequireNode<CollisionShape3D>(CollisionPath, nameof(CollisionPath));
		_visualRoot = RequireNode<Node3D>(VisualRootPath, nameof(VisualRootPath));
		_visualYawRoot = RequireNode<Node3D>(VisualYawRootPath, nameof(VisualYawRootPath));
		_visualPitchRoot = RequireNode<Node3D>(VisualPitchRootPath, nameof(VisualPitchRootPath));
		_cameraYawRoot = RequireNode<Node3D>(CameraYawRootPath, nameof(CameraYawRootPath));
		_cameraPitchRoot = RequireNode<Node3D>(CameraPitchRootPath, nameof(CameraPitchRootPath));
		_bound = true;
	}
}
