using System.Numerics;
using Cubit.Core.Scene;
using Cubit.Core.Spatial;

namespace Cubit.Physics.Shapes;

/// <summary>三维碰撞形状资源的基类。</summary>
public abstract class Shape3D : Resource
{
    /// <summary>取得以形状原点为中心的局部包围盒。</summary>
    public abstract Aabb3 GetLocalBounds();

    protected static void ValidatePositiveFinite(float value, string propertyName)
    {
        if (!float.IsFinite(value) || value <= 0f)
        {
            throw new InvalidOperationException($"{propertyName} 必须为有限正数");
        }
    }

    protected static void ValidatePositiveFinite(Vector3 value, string propertyName)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) ||
            value.X <= 0f || value.Y <= 0f || value.Z <= 0f)
        {
            throw new InvalidOperationException($"{propertyName} 的每个分量必须为有限正数");
        }
    }
}
