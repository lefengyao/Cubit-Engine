using System.Numerics;

namespace Cubit.Core.Rendering;

/// <summary>通用静态网格提交数据，支持位置/UV/明暗与可选的逐顶点 RGBA。</summary>
public sealed record MeshData
{
    public const int PositionUvShadeVertexStride = 6;

    public const int PositionUvShadeColorVertexStride = 10;

    /// <summary>位置、可重复局部 UV、明暗和每四边形共用的图集瓦片 ID。</summary>
    public const int PositionUvShadeTileVertexStride = 7;

    public float[] Vertices { get; }

    public uint[] Indices { get; }

    public Vector3 Translation { get; }

    public Vector3 BoundsMin { get; }

    public Vector3 BoundsMax { get; }

    public RID Material { get; }

    /// <summary>网格实例的通用 RGBA 调制，默认白色且完全不透明。</summary>
    public Vector4 Modulation { get; }

    /// <summary>顶点数据布局；不同布局共享同一通用材质与网格生命周期。</summary>
    public MeshVertexLayout VertexLayout { get; }

    /// <summary>每个顶点占用的 float 数。</summary>
    public int VertexStride => VertexLayout switch
    {
        MeshVertexLayout.PositionUvShade => PositionUvShadeVertexStride,
        MeshVertexLayout.PositionUvShadeColor => PositionUvShadeColorVertexStride,
        MeshVertexLayout.PositionUvShadeTile => PositionUvShadeTileVertexStride,
        _ => throw new InvalidOperationException($"不支持的网格顶点布局: {VertexLayout}"),
    };

    public MeshData(
        float[] vertices,
        uint[] indices,
        Vector3 translation,
        Vector3 boundsMin,
        Vector3 boundsMax)
        : this(vertices, indices, translation, boundsMin, boundsMax, RID.None, Vector4.One)
    {
    }

    public MeshData(
        float[] vertices,
        uint[] indices,
        Vector3 translation,
        Vector3 boundsMin,
        Vector3 boundsMax,
        RID material)
        : this(vertices, indices, translation, boundsMin, boundsMax, material, Vector4.One)
    {
    }

    public MeshData(
        float[] vertices,
        uint[] indices,
        Vector3 translation,
        Vector3 boundsMin,
        Vector3 boundsMax,
        RID material,
        Vector4 modulation,
        MeshVertexLayout vertexLayout = MeshVertexLayout.PositionUvShade)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        if (!Enum.IsDefined(vertexLayout))
        {
            throw new ArgumentOutOfRangeException(nameof(vertexLayout), "网格顶点布局无效");
        }

        var vertexStride = GetVertexStride(vertexLayout);
        if (vertices.Length == 0 || vertices.Length % vertexStride != 0)
        {
            throw new ArgumentException($"顶点数据必须非空且使用 {vertexStride}-float 步长", nameof(vertices));
        }

        if (indices.Length == 0)
        {
            throw new ArgumentException("索引数据不能为空", nameof(indices));
        }

        if (!IsFinite(translation) || !IsFinite(boundsMin) || !IsFinite(boundsMax))
        {
            throw new ArgumentException("网格变换和包围盒必须为有限数值");
        }

        if (boundsMin.X > boundsMax.X || boundsMin.Y > boundsMax.Y || boundsMin.Z > boundsMax.Z)
        {
            throw new ArgumentException("网格包围盒最小值不能大于最大值", nameof(boundsMin));
        }

        ValidateModulation(modulation);

        Vertices = vertices;
        Indices = indices;
        Translation = translation;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        Material = material;
        Modulation = modulation;
        VertexLayout = vertexLayout;
    }

    public MeshData WithModulation(Vector4 modulation) => new(
        Vertices,
        Indices,
        Translation,
        BoundsMin,
        BoundsMax,
        Material,
        modulation,
        VertexLayout);

    public static void ValidateModulation(Vector4 modulation)
    {
        if (!float.IsFinite(modulation.X) || !float.IsFinite(modulation.Y) ||
            !float.IsFinite(modulation.Z) || !float.IsFinite(modulation.W) ||
            modulation.X < 0f || modulation.X > 1f ||
            modulation.Y < 0f || modulation.Y > 1f ||
            modulation.Z < 0f || modulation.Z > 1f ||
            modulation.W < 0f || modulation.W > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(modulation), "网格颜色调制必须为 0..1 的有限 RGBA 值");
        }
    }

    private static int GetVertexStride(MeshVertexLayout vertexLayout) => vertexLayout switch
    {
        MeshVertexLayout.PositionUvShade => PositionUvShadeVertexStride,
        MeshVertexLayout.PositionUvShadeColor => PositionUvShadeColorVertexStride,
        MeshVertexLayout.PositionUvShadeTile => PositionUvShadeTileVertexStride,
        _ => throw new ArgumentOutOfRangeException(nameof(vertexLayout), "网格顶点布局无效"),
    };

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
