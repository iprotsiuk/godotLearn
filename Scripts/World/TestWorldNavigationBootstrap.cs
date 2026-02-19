using Godot;

namespace NetRunnerSlice.World;

public partial class TestWorldNavigationBootstrap : Node3D
{
    [Export] public NodePath NavigationRegionPath = new("NavigationRegion3D");

    public override void _Ready()
    {
        CallDeferred(nameof(BakeNavigationMeshRuntime));
    }

    private void BakeNavigationMeshRuntime()
    {
        NavigationRegion3D? navigationRegion = GetNodeOrNull<NavigationRegion3D>(NavigationRegionPath);
        if (navigationRegion is null)
        {
            GD.PushWarning("TestWorld nav bootstrap could not find NavigationRegion3D.");
            return;
        }

        if (navigationRegion.NavigationMesh is null)
        {
            navigationRegion.NavigationMesh = new NavigationMesh();
        }

        World3D? world = navigationRegion.GetWorld3D();
        if (world is not null && world.NavigationMap.IsValid)
        {
            NavigationMesh navMesh = navigationRegion.NavigationMesh;
            NavigationServer3D.MapSetCellSize(world.NavigationMap, navMesh.CellSize);
            NavigationServer3D.MapSetCellHeight(world.NavigationMap, navMesh.CellHeight);
        }

        navigationRegion.BakeNavigationMesh(false);
    }
}
