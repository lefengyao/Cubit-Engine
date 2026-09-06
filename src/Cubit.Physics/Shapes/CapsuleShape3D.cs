using System.Numerics;
using Cubit.Core.Scene;
using Cubit.Core.Spatial;

namespace Cubit.Physics.Shapes;

/// <summary>Y 轴朝向的胶囊形状；CylinderHeight 不包含两端半球。</summary>
public sealed class CapsuleShape3D : Shape3D
{
    [Export("半径")]
    public float Radius { get; set; } = 0.5f;

    [Export("圆柱高度")]
    public float CylinderHeight { get; set; } = 1f;

    public override Aabb3 GetLocalBounds()
    {
        ValidatePositiveFinite(Radius, nameof(Radius));
        ValidatePositiveFinite(CylinderHeight, nameof(CylinderHeight));
        var halfHeight = CylinderHeight * 0.5f + Radius;
        var extent = new Vector3(Radius, halfHeight, Radius);
        return new Aabb3(-extent, extent);
    }
}
