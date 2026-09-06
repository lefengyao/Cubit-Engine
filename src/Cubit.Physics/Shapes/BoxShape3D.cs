using System.Numerics;
using Cubit.Core.Scene;
using Cubit.Core.Spatial;

namespace Cubit.Physics.Shapes;

/// <summary>以原点为中心的轴对齐盒形状。</summary>
public sealed class BoxShape3D : Shape3D
{
    [Export("尺寸")]
    public Vector3 Size { get; set; } = Vector3.One;

    public override Aabb3 GetLocalBounds()
    {
        ValidatePositiveFinite(Size, nameof(Size));
        var halfSize = Size * 0.5f;
        return new Aabb3(-halfSize, halfSize);
    }
}
