using System.Numerics;
using Cubit.Core.Rendering;

namespace Cubit.Core.Scene;

/// <summary>通用立方体网格节点，用于基础 3D 场景和编辑器视口验证。</summary>
public sealed class BoxMesh3D : Node3D, IRenderingServerConsumer
{
    private RID _meshRid;

    [Export("尺寸")]
    public float Size { get; set; } = 1f;

    public RenderingServer? Server { get; set; }

    protected override void Ready()
    {
        if (Server is null)
        {
            throw new InvalidOperationException("BoxMesh3D 未注入渲染服务器");
        }

        if (!IsFinite(Position) || !float.IsFinite(Size) || Size <= 0f)
        {
            throw new InvalidOperationException("BoxMesh3D 的位置必须有限且尺寸必须大于 0");
        }

        var halfSize = Size * 0.5f;
        var localMin = new Vector3(-halfSize);
        var localMax = new Vector3(halfSize);
        var (vertices, indices) = BuildCube(localMin, localMax);
        _meshRid = Server.CreateMesh(new MeshData(
            vertices,
            indices,
            Position,
            Position + localMin,
            Position + localMax));
    }

    protected override void ExitTree()
    {
        if (_meshRid.IsValid)
        {
            Server?.DestroyMesh(_meshRid);
            _meshRid = RID.None;
        }
    }

    private static (float[] Vertices, uint[] Indices) BuildCube(Vector3 min, Vector3 max)
    {
        var vertices = new List<float>(24 * 6);
        var indices = new List<uint>(36);

        AddFace(vertices, indices, 1.00f,
            new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z),
            new Vector3(max.X, max.Y, max.Z), new Vector3(min.X, max.Y, max.Z));
        AddFace(vertices, indices, 0.55f,
            new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z),
            new Vector3(max.X, min.Y, max.Z), new Vector3(min.X, min.Y, max.Z));
        AddFace(vertices, indices, 0.82f,
            new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, max.Z),
            new Vector3(max.X, max.Y, max.Z), new Vector3(min.X, max.Y, max.Z));
        AddFace(vertices, indices, 0.70f,
            new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z),
            new Vector3(max.X, max.Y, min.Z), new Vector3(min.X, max.Y, min.Z));
        AddFace(vertices, indices, 0.90f,
            new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, min.Y, max.Z),
            new Vector3(max.X, max.Y, max.Z), new Vector3(max.X, max.Y, min.Z));
        AddFace(vertices, indices, 0.64f,
            new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, min.Y, max.Z),
            new Vector3(min.X, max.Y, max.Z), new Vector3(min.X, max.Y, min.Z));

        return ([.. vertices], [.. indices]);
    }

    private static void AddFace(
        List<float> vertices,
        List<uint> indices,
        float shade,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d)
    {
        var baseIndex = (uint)(vertices.Count / 6);
        AddVertex(vertices, a, 0f, 0f, shade);
        AddVertex(vertices, b, 1f, 0f, shade);
        AddVertex(vertices, c, 1f, 1f, shade);
        AddVertex(vertices, d, 0f, 1f, shade);
        indices.AddRange([baseIndex, baseIndex + 1, baseIndex + 2, baseIndex, baseIndex + 2, baseIndex + 3]);
    }

    private static void AddVertex(List<float> vertices, Vector3 position, float u, float v, float shade)
    {
        vertices.Add(position.X);
        vertices.Add(position.Y);
        vertices.Add(position.Z);
        vertices.Add(u);
        vertices.Add(v);
        vertices.Add(shade);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
