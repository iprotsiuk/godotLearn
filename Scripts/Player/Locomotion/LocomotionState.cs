// Scripts/Player/Locomotion/LocomotionState.cs
using Godot;

namespace NetRunnerSlice.Player.Locomotion;

public struct LocomotionState
{
	public LocomotionMode Mode;
	public int ModeTicks;
	public Vector3 WallNormal;
	public int WallRunTicksRemaining;
	public int SlideTicksRemaining;

	public static LocomotionState CreateInitial(bool grounded)
	{
		return new LocomotionState
		{
			Mode = grounded ? LocomotionMode.Grounded : LocomotionMode.Air,
			ModeTicks = 0,
			WallNormal = Vector3.Zero,
			WallRunTicksRemaining = 0,
			SlideTicksRemaining = 0
		};
	}
}
