namespace Cubit.Voxel.World;

/// <summary>一个区块网格层的紧凑顶点与索引数据。</summary>
public readonly record struct ChunkMeshData(float[] Vertices, uint[] Indices)
{
    public const int VertexFloatStride = 7;
    public static ChunkMeshData Empty { get; } = new([], []);
    public int VertexCount => Vertices.Length / VertexFloatStride;
    public int IndexCount => Indices.Length;
}

/// <summary>区块网格两层的顶点和索引数量；不复制网格数组。</summary>
public readonly record struct ChunkMeshGeometry(
    int OpaqueVertexCount,
    int OpaqueIndexCount,
    int FluidVertexCount,
    int FluidIndexCount)
{
    public int TotalVertexCount => checked(OpaqueVertexCount + FluidVertexCount);
    public int TotalIndexCount => checked(OpaqueIndexCount + FluidIndexCount);
}

/// <summary>区块的普通与半透明流体网格层；空层不应创建 GPU 网格资源。</summary>
public readonly record struct ChunkMeshLayers(ChunkMeshData Opaque, ChunkMeshData Fluid)
{
    public ChunkMeshGeometry Geometry => new(
        Opaque.VertexCount,
        Opaque.IndexCount,
        Fluid.VertexCount,
        Fluid.IndexCount);
}
