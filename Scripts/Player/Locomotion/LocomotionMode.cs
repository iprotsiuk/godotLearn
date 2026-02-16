// Scripts/Player/Locomotion/LocomotionMode.cs
namespace NetRunnerSlice.Player.Locomotion;

public enum LocomotionMode : byte
{
	Grounded = 0,
	Air = 1,
	WallRun = 2,
	WallCling = 3,
	Slide = 4
}
