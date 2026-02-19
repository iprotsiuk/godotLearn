using Godot;

namespace NetRunnerSlice.Rendering;

public static class RenderCompat
{
    public static void ForceOpaqueAndCheap(Node root)
    {
        if (!GodotObject.IsInstanceValid(root))
        {
            return;
        }

        ApplyRecursive(root);
    }

    private static void ApplyRecursive(Node node)
    {
        if (node is GeometryInstance3D geometry)
        {
            geometry.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            geometry.Set("gi_mode", 0);
        }

        if (node is MeshInstance3D meshInstance)
        {
            MakeOpaque(meshInstance);
        }

        foreach (Node child in node.GetChildren())
        {
            ApplyRecursive(child);
        }
    }

    private static void MakeOpaque(MeshInstance3D meshInstance)
    {
        if (meshInstance.MaterialOverride is BaseMaterial3D overrideMaterial)
        {
            meshInstance.MaterialOverride = CopyAsOpaque(overrideMaterial);
        }

        Mesh? mesh = meshInstance.Mesh;
        if (mesh is null)
        {
            return;
        }

        int surfaceCount = mesh.GetSurfaceCount();
        for (int surface = 0; surface < surfaceCount; surface++)
        {
            Material? material = meshInstance.GetSurfaceOverrideMaterial(surface) ?? mesh.SurfaceGetMaterial(surface);
            if (material is not BaseMaterial3D baseMaterial)
            {
                continue;
            }

            meshInstance.SetSurfaceOverrideMaterial(surface, CopyAsOpaque(baseMaterial));
        }
    }

    private static BaseMaterial3D CopyAsOpaque(BaseMaterial3D source)
    {
        BaseMaterial3D material = (BaseMaterial3D)source.Duplicate();
        material.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;
        material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly;
        material.NoDepthTest = false;
        return material;
    }
}
