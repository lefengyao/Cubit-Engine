using System.Numerics;

namespace Cubit.Core.Rendering;

/// <summary>
/// 视锥体（Frustum）：由 视图×投影矩阵提取 6 个平面，用于剔除不可见包围盒。
/// 采用与 GPU 一致的矩阵约定：C# 行向量 v' = v * (view * projection)，
/// 与着色器 gl_Position = ubo.proj * ubo.view * pos 等价（经列主序解释）。
/// </summary>
public readonly struct Frustum
{
    // 6 个平面：0=左 1=右 2=下 3=上 4=近 5=远（法线朝内，点积 < 0 为外侧）
    private readonly Vector4 _left;
    private readonly Vector4 _right;
    private readonly Vector4 _bottom;
    private readonly Vector4 _top;
    private readonly Vector4 _near;
    private readonly Vector4 _far;

    public Frustum(in Matrix4x4 viewProjection)
    {
        // 行向量约定（clip = v * M）下提取（Gribb-Hartmann）：plane = col3 ± col_i，再归一化
        var col0 = new Vector4(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
        var col1 = new Vector4(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
        var col2 = new Vector4(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
        var col3 = new Vector4(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);

        _left = Normalize(col3 + col0);
        _right = Normalize(col3 - col0);
        _bottom = Normalize(col3 + col1);
        _top = Normalize(col3 - col1);
        // Vulkan 裁剪空间 z ∈ [0,1]：近平面 clip.z >= 0，远平面 clip.z <= clip.w
        _near = Normalize(col2);
        _far = Normalize(col3 - col2);
    }

    /// <summary>点是否在视锥内（含边界）。</summary>
    public bool Contains(Vector3 point)
    {
        var p = new Vector4(point, 1f);
        return Vector4.Dot(_left, p) >= 0
            && Vector4.Dot(_right, p) >= 0
            && Vector4.Dot(_bottom, p) >= 0
            && Vector4.Dot(_top, p) >= 0
            && Vector4.Dot(_near, p) >= 0
            && Vector4.Dot(_far, p) >= 0;
    }

    /// <summary>
    /// AABB 是否与视锥相交（标准 p-vertex 测试：所有平面都取“最有利顶点”仍在平面外才算剔除）。
    /// </summary>
    public bool Intersects(in Vector3 min, in Vector3 max) =>
        PlaneIntersects(_left, min, max)
        && PlaneIntersects(_right, min, max)
        && PlaneIntersects(_bottom, min, max)
        && PlaneIntersects(_top, min, max)
        && PlaneIntersects(_near, min, max)
        && PlaneIntersects(_far, min, max);

    private bool PlaneIntersects(in Vector4 plane, in Vector3 min, in Vector3 max)
    {
        // p-vertex：沿平面法线方向最远的角
        var px = plane.X >= 0 ? max.X : min.X;
        var py = plane.Y >= 0 ? max.Y : min.Y;
        var pz = plane.Z >= 0 ? max.Z : min.Z;
        return Vector4.Dot(plane, new Vector4(px, py, pz, 1f)) >= 0;
    }

    private static Vector4 Normalize(in Vector4 plane)
    {
        var len = MathF.Sqrt(plane.X * plane.X + plane.Y * plane.Y + plane.Z * plane.Z);
        return len > 1e-8f ? plane / len : plane;
    }
}
