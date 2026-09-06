using System.Numerics;

namespace Cubit.Core.Spatial;

/// <summary>有限三维轴对齐包围盒，供通用空间查询使用。</summary>
public readonly struct Aabb3
{
    private const float ParallelEpsilon = 1e-8f;

    public Aabb3(Vector3 min, Vector3 max)
    {
        if (!IsFinite(min) || !IsFinite(max))
        {
            throw new ArgumentException("AABB 的最小值和最大值必须是有限数值");
        }

        if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
        {
            throw new ArgumentException("AABB 最小值不能大于最大值");
        }

        Min = min;
        Max = max;
    }

    public Vector3 Min { get; }

    public Vector3 Max { get; }

    public float SurfaceArea
    {
        get
        {
            var size = Max - Min;
            return 2f * (size.X * size.Y + size.X * size.Z + size.Y * size.Z);
        }
    }

    /// <summary>当前包围盒是否完整包含另一个包围盒（含边界）。</summary>
    public bool Contains(in Aabb3 other) =>
        Min.X <= other.Min.X && Min.Y <= other.Min.Y && Min.Z <= other.Min.Z &&
        Max.X >= other.Max.X && Max.Y >= other.Max.Y && Max.Z >= other.Max.Z;

    /// <summary>两个包围盒是否相交（含边界）。</summary>
    public bool Intersects(in Aabb3 other) =>
        Min.X <= other.Max.X && Max.X >= other.Min.X &&
        Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
        Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;

    /// <summary>沿每个轴膨胀固定距离。</summary>
    public Aabb3 Inflated(float amount)
    {
        if (!float.IsFinite(amount) || amount < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "AABB 膨胀距离必须是非负有限数值");
        }

        var offset = new Vector3(amount);
        return new Aabb3(Min - offset, Max + offset);
    }

    /// <summary>合并两个包围盒。</summary>
    public static Aabb3 Combine(in Aabb3 left, in Aabb3 right) =>
        new(Vector3.Min(left.Min, right.Min), Vector3.Max(left.Max, right.Max));

    /// <summary>测试射线在最大距离内与包围盒的首次相交距离。</summary>
    public bool Raycast(in Vector3 origin, in Vector3 direction, float maxDistance, out float distance)
    {
        ValidateRay(origin, direction, maxDistance);
        return RaycastUnchecked(origin, direction, maxDistance, out distance);
    }

    internal bool RaycastUnchecked(in Vector3 origin, in Vector3 direction, float maxDistance, out float distance)
    {
        var minimum = 0f;
        var maximum = maxDistance;
        if (!IntersectAxis(origin.X, direction.X, Min.X, Max.X, ref minimum, ref maximum) ||
            !IntersectAxis(origin.Y, direction.Y, Min.Y, Max.Y, ref minimum, ref maximum) ||
            !IntersectAxis(origin.Z, direction.Z, Min.Z, Max.Z, ref minimum, ref maximum))
        {
            distance = 0f;
            return false;
        }

        distance = minimum;
        return true;
    }

    internal static void ValidateRay(in Vector3 origin, in Vector3 direction, float maxDistance)
    {
        if (!IsFinite(origin) || !IsFinite(direction))
        {
            throw new ArgumentException("射线原点和方向必须是有限数值");
        }

        if (direction.LengthSquared() <= ParallelEpsilon * ParallelEpsilon)
        {
            throw new ArgumentException("射线方向不能为零", nameof(direction));
        }

        if (!float.IsFinite(maxDistance) || maxDistance < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDistance), "射线最大距离必须是非负有限数值");
        }
    }

    private static bool IntersectAxis(float origin, float direction, float min, float max, ref float minimum, ref float maximum)
    {
        if (MathF.Abs(direction) <= ParallelEpsilon)
        {
            return origin >= min && origin <= max;
        }

        var inverse = 1f / direction;
        var entry = (min - origin) * inverse;
        var exit = (max - origin) * inverse;
        if (entry > exit)
        {
            (entry, exit) = (exit, entry);
        }

        minimum = MathF.Max(minimum, entry);
        maximum = MathF.Min(maximum, exit);
        return maximum >= minimum;
    }

    private static bool IsFinite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

