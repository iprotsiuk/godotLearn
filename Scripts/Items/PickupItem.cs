using Godot;
using NetRunnerSlice.Net;
using NetRunnerSlice.Player;

namespace NetRunnerSlice.Items;

public partial class PickupItem : Area3D
{
    [Export] public int PickupId { get; set; }
    [Export] public ItemId ItemId { get; set; } = ItemId.None;
    [Export] public byte Charges { get; set; }

    private NetSession? _session;

    public override void _Ready()
    {
        Node? netSessionNode = GetTree().GetFirstNodeInGroup("net_session");
        _session = netSessionNode as NetSession;
        if (_session is null)
        {
            GD.PushWarning($"PickupItem {Name}: NetSession group node not found.");
            return;
        }

        _session.RegisterPickup(this);
        BodyEntered += OnBodyEntered;
    }

    public override void _ExitTree()
    {
        BodyEntered -= OnBodyEntered;
        _session?.UnregisterPickup(PickupId);
    }

    public void SetActive(bool active)
    {
        Visible = active;
        Monitoring = active;
        Monitorable = active;
        SetCollisionShapesActive(this, active);
    }

    private void OnBodyEntered(Node3D body)
    {
        if (_session is null || !_session.IsServer)
        {
            return;
        }

        if (body is not PlayerCharacter pc)
        {
            return;
        }

        if (!_session.TryGetServerCharacter(pc.PeerId, out PlayerCharacter serverChar))
        {
            return;
        }

        if (!object.ReferenceEquals(pc, serverChar))
        {
            return;
        }

        _session.ServerTryConsumePickup(pc.PeerId, PickupId);
    }

    private static void SetCollisionShapesActive(Node node, bool active)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is CollisionShape3D collisionShape)
            {
                collisionShape.Disabled = !active;
            }

            SetCollisionShapesActive(child, active);
        }
    }
}
