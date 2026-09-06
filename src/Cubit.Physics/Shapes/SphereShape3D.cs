using Cubit.Core.Scene;
using Cubit.Core.Spatial;

namespace Cubit.Physics.Shapes;

/// <summary>以原点为中心的球形状。</summary>
public sealed class SphereShape3D : Shape3D
{
    [Export("半径")]
    public float Radius { get; set; } = 0.5f;

    public override Aabb3 GetLocalBounds()
    {
        ValidatePositiveFinite(Radius, nameof(Radius));
        var extent = new System.Numerics.Vector3(Radius);
        return new Aabb3(-extent, extent);
    }
}
