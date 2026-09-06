using System.Buffers;

namespace Cubit.Voxel.World;

/// <summary>
/// 中心区块及一圈水平邻域的不可变快照。
/// 快照脱离 ChunkStore 后可安全交给 worker，避免网格编译持有世界锁。
/// </summary>
public sealed class ChunkMeshSnapshot : IDisposable
{
    private const int Border = 1;
    private const int Width = Chunk.SizeX + Border * 2;
    private const int Depth = Chunk.SizeZ + Border * 2;
    private readonly uint[] _blocks;
    private readonly byte[] _lights;
    private readonly bool _hasWestNeighbour;
    private readonly bool _hasEastNeighbour;
    private readonly bool _hasNorthNeighbour;
    private readonly bool _hasSouthNeighbour;
    private readonly bool _pooled;
    private bool _disposed;

    private ChunkMeshSnapshot(
        int chunkX,
        int chunkZ,
        uint[] blocks,
        byte[] lights,
        bool hasWestNeighbour,
        bool hasEastNeighbour,
        bool hasNorthNeighbour,
        bool hasSouthNeighbour,
        bool pooled)
    {
        ChunkX = chunkX;
        ChunkZ = chunkZ;
        _blocks = blocks;
        _lights = lights;
        _hasWestNeighbour = hasWestNeighbour;
        _hasEastNeighbour = hasEastNeighbour;
        _hasNorthNeighbour = hasNorthNeighbour;
        _hasSouthNeighbour = hasSouthNeighbour;
        _pooled = pooled;
    }

    public int ChunkX { get; }

    public int ChunkZ { get; }

    public BlockState GetBlock(int localX, int y, int localZ)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Chunk.IsValidY(y) || localX < -Border || localX >= Chunk.SizeX + Border ||
            localZ < -Border || localZ >= Chunk.SizeZ + Border)
        {
            return BlockState.Air;
        }

        return BlockState.FromPacked(_blocks[Index(localX, y, localZ)]);
    }

    public (byte Sky, byte Block) GetLight(int localX, int y, int localZ)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Chunk.IsValidY(y) || localX < -Border || localX >= Chunk.SizeX + Border ||
            localZ < -Border || localZ >= Chunk.SizeZ + Border)
        {
            return (0, 0);
        }

        var value = _lights[Index(localX, y, localZ)];
        return ((byte)(value >> 4), (byte)(value & 0xF));
    }

    /// <summary>返回水平相邻位置是否已有区块数据；未加载不是空气，不能生成临时侧面。</summary>
    public bool HasHorizontalNeighbourData(int localX, int localZ)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (localX < 0)
        {
            return _hasWestNeighbour;
        }

        if (localX >= Chunk.SizeX)
        {
            return _hasEastNeighbour;
        }

        if (localZ < 0)
        {
            return _hasNorthNeighbour;
        }

        if (localZ >= Chunk.SizeZ)
        {
            return _hasSouthNeighbour;
        }

        return true;
    }

    internal static ChunkMeshSnapshot Capture(ChunkStore store, int chunkX, int chunkZ) =>
        CaptureCore(store, chunkX, chunkZ, pooled: false);

    internal static ChunkMeshSnapshot CapturePooled(ChunkStore store, int chunkX, int chunkZ) =>
        CaptureCore(store, chunkX, chunkZ, pooled: true);

    private static ChunkMeshSnapshot CaptureCore(ChunkStore store, int chunkX, int chunkZ, bool pooled)
    {
        ArgumentNullException.ThrowIfNull(store);
        var length = Width * Chunk.SizeY * Depth;
        var blocks = pooled ? ArrayPool<uint>.Shared.Rent(length) : new uint[length];
        var lights = pooled ? ArrayPool<byte>.Shared.Rent(length) : new byte[length];

        try
        {
            lock (store.SyncRoot)
            {
                var neighbours = new Chunk?[3, 3];
                for (var dz = -1; dz <= 1; dz++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        neighbours[dx + 1, dz + 1] = store.GetChunkUnsafe(chunkX + dx, chunkZ + dz);
                    }
                }

                var sections = new Section?[3, 3];
                for (var sectionIndex = 0; sectionIndex < Chunk.SectionCount; sectionIndex++)
                {
                    for (var dz = 0; dz < 3; dz++)
                    {
                        for (var dx = 0; dx < 3; dx++)
                        {
                            sections[dx, dz] = neighbours[dx, dz]?.GetSection(sectionIndex);
                        }
                    }

                    var sectionBaseY = Chunk.MinY + sectionIndex * Chunk.SectionSizeY;
                    for (var localY = 0; localY < Chunk.SectionSizeY; localY++)
                    {
                        var y = sectionBaseY + localY;
                        for (var localZ = -Border; localZ < Chunk.SizeZ + Border; localZ++)
                        {
                            var zRegion = localZ < 0 ? 0 : localZ >= Chunk.SizeZ ? 2 : 1;
                            var sampleLocalZ = localZ < 0 ? Chunk.SizeZ - 1 : localZ >= Chunk.SizeZ ? 0 : localZ;
                            for (var localX = -Border; localX < Chunk.SizeX + Border; localX++)
                            {
                                var xRegion = localX < 0 ? 0 : localX >= Chunk.SizeX ? 2 : 1;
                                var sampleLocalX = localX < 0 ? Chunk.SizeX - 1 : localX >= Chunk.SizeX ? 0 : localX;
                                var sampleSection = sections[xRegion, zRegion];
                                var block = sampleSection?.Get(sampleLocalX, localY, sampleLocalZ) ?? BlockState.Air;
                                var light = sampleSection is null
                                    ? (Sky: (byte)0, Block: (byte)0)
                                    : sampleSection.GetLight(sampleLocalX, localY, sampleLocalZ);
                                var index = Index(localX, y, localZ);
                                blocks[index] = block.Packed;
                                lights[index] = (byte)((light.Sky << 4) | (light.Block & 0xF));
                            }
                        }
                    }
                }

                return new ChunkMeshSnapshot(
                    chunkX,
                    chunkZ,
                    blocks,
                    lights,
                    neighbours[0, 1] is not null,
                    neighbours[2, 1] is not null,
                    neighbours[1, 0] is not null,
                    neighbours[1, 2] is not null,
                    pooled);
            }
        }
        catch
        {
            if (pooled)
            {
                ArrayPool<uint>.Shared.Return(blocks);
                ArrayPool<byte>.Shared.Return(lights);
            }

            throw;
        }
    }

    private static (byte Sky, byte Block) GetLight(Chunk chunk, int localX, int y, int localZ)
    {
        if (!Chunk.IsValidY(y))
        {
            return (0, 0);
        }

        var section = chunk.GetSection(Chunk.SectionIndex(y));
        return section.GetLight(localX, Chunk.SectionLocalY(y), localZ);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pooled)
        {
            ArrayPool<uint>.Shared.Return(_blocks);
            ArrayPool<byte>.Shared.Return(_lights);
        }
    }

    private static int Index(int localX, int y, int localZ) =>
        ((y - Chunk.MinY) * Depth + (localZ + Border)) * Width + (localX + Border);
}
