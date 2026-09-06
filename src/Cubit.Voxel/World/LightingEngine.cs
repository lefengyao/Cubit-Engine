using System.Buffers;

namespace Cubit.Voxel.World;

/// <summary>
/// 光照引擎（借鉴 MC 光照 + Starlight 思路）：
/// - 天空光：逐列自上而下传播，遇不透光方块截断（0）；
/// - 方块光：发光方块 BFS 洪泛（当前块内，后续可做跨区块与并行）；
/// - 光照存储于 Section（高 4 位天空光 / 低 4 位方块光），供网格化采样明暗。
/// </summary>
public static class LightingEngine
{
    private readonly record struct SkySeed(int X, int Y, int Z, byte Level);

    private struct SkySeedBuffer : IDisposable
    {
        private SkySeed[]? _items;
        private int _count;

        public SkySeedBuffer(int capacity)
        {
            _items = capacity > 0 ? ArrayPool<SkySeed>.Shared.Rent(capacity) : null;
            _count = 0;
        }

        public readonly ReadOnlySpan<SkySeed> AsSpan() =>
            _items is null ? ReadOnlySpan<SkySeed>.Empty : _items.AsSpan(0, _count);

        public void Add(SkySeed seed)
        {
            if (_items is null)
            {
                throw new InvalidOperationException("天空光种子缓冲未初始化");
            }

            if ((uint)_count >= (uint)_items.Length)
            {
                throw new InvalidOperationException("天空光种子缓冲容量不足");
            }

            _items[_count++] = seed;
        }

        public void Dispose()
        {
            var items = _items;
            _items = null;
            _count = 0;
            if (items is not null)
            {
                ArrayPool<SkySeed>.Shared.Return(items, clearArray: false);
            }
        }
    }

    private const int MaxBoundarySeedCount = Chunk.SizeX * Chunk.SizeY * 4;
    private const ulong AllSectionMask = (1UL << Chunk.SectionCount) - 1;
    public const byte MaxLight = 15;
    private static int _lastSkySeedCount;
    private static int _lastSkyAroundRelightCount;

    private static readonly (int X, int Y, int Z)[] NeighbourOffsets =
    [
        (1, 0, 0), (-1, 0, 0),
        (0, 1, 0), (0, -1, 0),
        (0, 0, 1), (0, 0, -1),
    ];

    private static readonly (int X, int Z)[] HorizontalNeighbourOffsets =
    [
        (1, 0), (-1, 0),
        (0, 1), (0, -1),
    ];

    public static (byte Sky, byte Block) GetLight(ChunkStore store, int x, int y, int z)
    {
        if (!Chunk.IsValidY(y))
        {
            return (0, 0);
        }

        var (cx, cz) = ChunkStore.WorldToChunk(x, z);
        var chunk = store.GetChunk(cx, cz);
        if (chunk is null)
        {
            return (0, 0);
        }

        var (lx, _, lz) = ChunkStore.WorldToLocal(x, y, z);
        return chunk.GetSection(Chunk.SectionIndex(y)).GetLight(lx, Chunk.SectionLocalY(y), lz);
    }

    /// <summary>判断方块变更是否接触既有方块光，避免把光路变化误当成纯天空光列变化。</summary>
    internal static bool HasBlockLightAtOrAdjacent(Chunk chunk, int localX, int y, int localZ)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if ((uint)localX < Chunk.SizeX && Chunk.IsValidY(y) && (uint)localZ < Chunk.SizeZ &&
            chunk.GetSection(Chunk.SectionIndex(y)).GetLight(localX, Chunk.SectionLocalY(y), localZ).Block > 0)
        {
            return true;
        }

        foreach (var (dx, dy, dz) in NeighbourOffsets)
        {
            var x = localX + dx;
            var neighbourY = y + dy;
            var z = localZ + dz;
            if ((uint)x >= Chunk.SizeX || !Chunk.IsValidY(neighbourY) || (uint)z >= Chunk.SizeZ)
            {
                continue;
            }

            if (chunk.GetSection(Chunk.SectionIndex(neighbourY)).GetLight(x, Chunk.SectionLocalY(neighbourY), z).Block > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>重算某区块光照（需区块已生成；调用方持锁或单线程）。</summary>
    public static void RelightChunk(ChunkStore store, int cx, int cz)
    {
        var chunk = store.GetChunk(cx, cz);
        if (chunk is null)
        {
            return;
        }

        RelightSky(store, cx, cz);
        RelightBlockLight(chunk);
    }

    /// <summary>
    /// 重算区块内天空光：露天空气直接接收 15 级光，随后在透明体素中逐格扩散。
    /// 不透明方块不保存天空光，避免旧的列光值被误当成发光材质。
    /// </summary>
    public static void RelightSky(Chunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        RelightSky(chunk, ReadOnlySpan<SkySeed>.Empty, cancellationToken);
    }

    /// <summary>重算区块天空光，并接收已加载水平邻区传入的边界光。</summary>
    public static void RelightSky(ChunkStore store, int cx, int cz, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var chunk = store.GetChunk(cx, cz);
        if (chunk is null)
        {
            return;
        }

        var boundarySeeds = CollectBoundarySkySeeds(store, chunk, cx, cz);
        try
        {
            RelightSky(chunk, boundarySeeds.AsSpan(), cancellationToken);
        }
        finally
        {
            boundarySeeds.Dispose();
        }
    }

    /// <summary>
    /// 同步一个区块及其四个已加载邻区的天空光边界。
    /// 第二次重算中心区块可消除遮挡块移除时从邻区读到的陈旧边界种子。
    /// </summary>
    public static void RelightSkyAround(ChunkStore store, int cx, int cz, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        Volatile.Write(ref _lastSkyAroundRelightCount, 0);
        RelightSky(store, cx, cz, cancellationToken);
        foreach (var (dx, dz) in HorizontalNeighbourOffsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (store.HasChunk(cx + dx, cz + dz))
            {
                RelightSky(store, cx + dx, cz + dz, cancellationToken);
            }
        }

        RelightSky(store, cx, cz, cancellationToken);
    }

    /// <summary>
    /// 按方块所在的水平边界局部同步天空光。区块内部修改不会无条件重算四个邻区；
    /// 只有触及边界时才同步对应邻区，最后再次校正中心区块的边界种子。
    /// </summary>
    public static void RelightSkyAround(
        ChunkStore store,
        int cx,
        int cz,
        int localX,
        int localZ,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if ((uint)localX >= Chunk.SizeX || (uint)localZ >= Chunk.SizeZ)
        {
            throw new ArgumentOutOfRangeException(nameof(localX), "天空光局部坐标必须位于区块范围内");
        }

        Volatile.Write(ref _lastSkyAroundRelightCount, 0);
        RelightSky(store, cx, cz, cancellationToken);
        var relightCount = 1;
        var synchronizedNeighbour = false;
        foreach (var (dx, dz) in HorizontalNeighbourOffsets)
        {
            if ((dx < 0 && localX != 0) || (dx > 0 && localX != Chunk.SizeX - 1) ||
                (dz < 0 && localZ != 0) || (dz > 0 && localZ != Chunk.SizeZ - 1))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (store.HasChunk(cx + dx, cz + dz))
            {
                RelightSky(store, cx + dx, cz + dz, cancellationToken);
                relightCount++;
                synchronizedNeighbour = true;
            }
        }

        if (synchronizedNeighbour)
        {
            RelightSky(store, cx, cz, cancellationToken);
            relightCount++;
        }

        Volatile.Write(ref _lastSkyAroundRelightCount, relightCount);
    }

    /// <summary>
    /// 方块编辑专用天空光入口。对区块内部的直射天空列使用局部列更新；
    /// 洞穴、遮挡和水平边界仍回退到受影响区块校正，保证边界光不残留。
    /// </summary>
    public static void RelightSkyAround(
        ChunkStore store,
        int cx,
        int cz,
        int localX,
        int y,
        int localZ,
        BlockState previous,
        BlockState next,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if ((uint)localX >= Chunk.SizeX || (uint)localZ >= Chunk.SizeZ)
        {
            throw new ArgumentOutOfRangeException(nameof(localX), "天空光局部坐标必须位于区块范围内");
        }

        Volatile.Write(ref _lastSkyAroundRelightCount, 0);
        var chunk = store.GetChunk(cx, cz);
        if (chunk is not null &&
            Chunk.IsValidY(y) &&
            localX != 0 && localX != Chunk.SizeX - 1 &&
            localZ != 0 && localZ != Chunk.SizeZ - 1 &&
            TryRelightSkyColumnFast(chunk, localX, y, localZ, previous, next, cancellationToken))
        {
            Volatile.Write(ref _lastSkyAroundRelightCount, 1);
            return;
        }

        RelightSkyAround(store, cx, cz, localX, localZ, cancellationToken);
    }

    private static bool TryRelightSkyColumnFast(
        Chunk chunk,
        int localX,
        int y,
        int localZ,
        BlockState previous,
        BlockState next,
        CancellationToken cancellationToken)
    {
        var wasOpaque = BlockRegistry.IsOpaque(previous.Id);
        var isOpaque = BlockRegistry.IsOpaque(next.Id);
        if (wasOpaque == isOpaque &&
            BlockRegistry.GetLightAttenuation(previous.Id) == BlockRegistry.GetLightAttenuation(next.Id))
        {
            return true;
        }

        // 目标列上方必须保持直射天空；否则该编辑可能改变洞穴侧向传播，交给完整校正。
        for (var scanY = Chunk.MaxYExclusive - 1; scanY > y; scanY--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BlockRegistry.IsOpaque(chunk.GetBlock(localX, scanY, localZ).Id))
            {
                return false;
            }
        }

        // 目标列下方只有在四侧仍是直射天空时才能用常数级局部列更新。
        for (var scanY = y - 1; scanY >= Chunk.MinY; scanY--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BlockRegistry.IsOpaque(chunk.GetBlock(localX, scanY, localZ).Id))
            {
                break;
            }

            if (!HasDirectSkyNeighbours(chunk, localX, scanY, localZ))
            {
                return false;
            }
        }

        if (isOpaque)
        {
            SetSkyLight(chunk, localX, y, localZ, 0);
            for (var scanY = y - 1; scanY >= Chunk.MinY; scanY--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (BlockRegistry.IsOpaque(chunk.GetBlock(localX, scanY, localZ).Id))
                {
                    break;
                }

                SetSkyLight(chunk, localX, scanY, localZ, MaxLight - 1);
            }
        }
        else
        {
            for (var scanY = y; scanY < Chunk.MaxYExclusive; scanY++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (BlockRegistry.IsOpaque(chunk.GetBlock(localX, scanY, localZ).Id))
                {
                    break;
                }

                SetSkyLight(chunk, localX, scanY, localZ, MaxLight);
            }

            for (var scanY = y - 1; scanY >= Chunk.MinY; scanY--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (BlockRegistry.IsOpaque(chunk.GetBlock(localX, scanY, localZ).Id))
                {
                    break;
                }

                SetSkyLight(chunk, localX, scanY, localZ, MaxLight);
            }
        }

        return true;
    }

    private static bool HasDirectSkyNeighbours(Chunk chunk, int localX, int y, int localZ)
    {
        foreach (var (dx, dz) in HorizontalNeighbourOffsets)
        {
            var neighbourX = localX + dx;
            var neighbourZ = localZ + dz;
            if ((uint)neighbourX >= Chunk.SizeX || (uint)neighbourZ >= Chunk.SizeZ ||
                BlockRegistry.IsOpaque(chunk.GetBlock(neighbourX, y, neighbourZ).Id) ||
                chunk.GetSection(Chunk.SectionIndex(y))
                    .GetLight(neighbourX, Chunk.SectionLocalY(y), neighbourZ).Sky != MaxLight)
            {
                return false;
            }
        }

        return true;
    }

    private static void SetSkyLight(Chunk chunk, int localX, int y, int localZ, byte sky)
    {
        var section = chunk.GetSection(Chunk.SectionIndex(y));
        var current = section.GetLight(localX, Chunk.SectionLocalY(y), localZ);
        section.SetLight(localX, Chunk.SectionLocalY(y), localZ, sky, current.Block);
    }

    /// <summary>
    /// 新生成区块提交后的增量天空光同步。生成线程已经完成中心区块独立光照，
    /// 因此只重算已有邻区，再用邻区边界光重算中心，避免中心区块重复执行一次。
    /// </summary>
    internal static void RelightSkyAfterChunkAdded(
        ChunkStore store,
        int cx,
        int cz,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        Volatile.Write(ref _lastSkyAroundRelightCount, 0);
        var hasNeighbour = false;
        var relightCount = 0;
        foreach (var (dx, dz) in HorizontalNeighbourOffsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!store.HasChunk(cx + dx, cz + dz))
            {
                continue;
            }

            hasNeighbour = true;
            RelightSky(store, cx + dx, cz + dz, cancellationToken);
            relightCount++;
        }

        if (hasNeighbour)
        {
            RelightSky(store, cx, cz, cancellationToken);
            relightCount++;
        }

        Volatile.Write(ref _lastSkyAroundRelightCount, relightCount);
    }

    /// <summary>
    /// 分步完成新区块提交后的天空光同步：步骤 0..3 处理四个水平邻区，步骤 4 处理中心区块。
    /// 调用方在主线程按帧推进步骤，避免一次提交占满渲染帧。
    /// </summary>
    internal static bool RelightSkyAfterChunkAddedStep(
        ChunkStore store,
        int cx,
        int cz,
        int step,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if ((uint)step >= (uint)(HorizontalNeighbourOffsets.Length + 1))
        {
            throw new ArgumentOutOfRangeException(nameof(step), "新区块天空光同步步骤必须在 0..4 范围内");
        }

        if (store.GetChunk(cx, cz) is null)
        {
            return true;
        }

        if (step < HorizontalNeighbourOffsets.Length)
        {
            var (dx, dz) = HorizontalNeighbourOffsets[step];
            if (store.GetChunk(cx + dx, cz + dz) is not null)
            {
                RelightSky(store, cx + dx, cz + dz, cancellationToken);
            }

            return false;
        }

        RelightSky(store, cx, cz, cancellationToken);
        return true;
    }

    private static void RelightSky(
        Chunk chunk,
        ReadOnlySpan<SkySeed> boundarySeeds,
        CancellationToken cancellationToken)
    {
        if (chunk.DirtySectionMask == AllSectionMask)
        {
            RelightSkyCore(chunk, boundarySeeds, cancellationToken);
            return;
        }

        Span<ulong> beforeFirst = stackalloc ulong[Chunk.SectionCount];
        Span<ulong> beforeSecond = stackalloc ulong[Chunk.SectionCount];
        CaptureLightHashes(chunk, beforeFirst, beforeSecond);
        var completed = false;
        try
        {
            RelightSkyCore(chunk, boundarySeeds, cancellationToken);
            completed = true;
        }
        finally
        {
            if (completed)
            {
                MarkChangedSections(chunk, beforeFirst, beforeSecond);
            }
            else
            {
                MarkAllSectionsDirty(chunk);
            }
        }
    }

    private static void RelightSkyCore(
        Chunk chunk,
        ReadOnlySpan<SkySeed> boundarySeeds,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<(int X, int Y, int Z, byte Level)>();
        var skySeedCount = 0;

        // 先清除旧天空光，保留独立的方块光通道。
        for (var sectionIndex = 0; sectionIndex < Chunk.SectionCount; sectionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var section = chunk.GetSection(sectionIndex);
            for (var lz = 0; lz < Chunk.SizeZ; lz++)
            {
                for (var localY = 0; localY < Chunk.SectionSizeY; localY++)
                {
                    for (var lx = 0; lx < Chunk.SizeX; lx++)
                    {
                        var light = section.GetLight(lx, localY, lz);
                        section.SetLight(lx, localY, lz, 0, light.Block, trackDirty: false);
                    }
                }
            }
        }

        // 无阻挡竖列中的空气保持直射天空光；具有衰减定义的非遮挡方块会降低其自身及下方直射值。
        // 后续队列只处理从这些直射区域进入遮挡区域后的间接传播。
        for (var lx = 0; lx < Chunk.SizeX; lx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var lz = 0; lz < Chunk.SizeZ; lz++)
            {
                var directSky = MaxLight;
                for (var sectionIndex = Chunk.SectionCount - 1; sectionIndex >= 0; sectionIndex--)
                {
                    var section = chunk.GetSection(sectionIndex);
                    for (var localY = Chunk.SectionSizeY - 1; localY >= 0; localY--)
                    {
                        var block = section.Get(lx, localY, lz);
                        if (BlockRegistry.IsOpaque(block.Id))
                        {
                            sectionIndex = -1;
                            break;
                        }

                        directSky = (byte)Math.Max(0, directSky - BlockRegistry.GetLightAttenuation(block.Id));
                        var light = section.GetLight(lx, localY, lz);
                        section.SetLight(lx, localY, lz, directSky, light.Block, trackDirty: false);
                    }
                }
            }
        }

        // 直射列内部不需要逐格进入 BFS；只把直射区与未照亮透明区的水平边界入队。
        // 这样开放天空不会为每个高度格分配队列项，屋檐/洞穴仍能从边界向内传播。
        for (var lx = 0; lx < Chunk.SizeX; lx++)
        {
            for (var lz = 0; lz < Chunk.SizeZ; lz++)
            {
                for (var sectionIndex = 0; sectionIndex < Chunk.SectionCount; sectionIndex++)
                {
                    var section = chunk.GetSection(sectionIndex);
                    for (var localY = 0; localY < Chunk.SectionSizeY; localY++)
                    {
                        var y = Chunk.MinY + sectionIndex * Chunk.SectionSizeY + localY;
                        var sourceSky = section.GetLight(lx, localY, lz).Sky;
                        if (BlockRegistry.IsOpaque(section.Get(lx, localY, lz).Id) || sourceSky == 0)
                        {
                            continue;
                        }

                        var boundary = false;
                        foreach (var (dx, dz) in HorizontalNeighbourOffsets)
                        {
                            var nx = lx + dx;
                            var nz = lz + dz;
                            if ((uint)nx >= Chunk.SizeX || (uint)nz >= Chunk.SizeZ ||
                                BlockRegistry.IsOpaque(section.Get(nx, localY, nz).Id) ||
                                section.GetLight(nx, localY, nz).Sky != 0)
                            {
                                continue;
                            }

                            boundary = true;
                            break;
                        }

                        if (boundary)
                        {
                            queue.Enqueue((lx, y, lz, sourceSky));
                            skySeedCount++;
                        }
                    }
                }
            }
        }

        // 邻区已完成的天空光以边界空气为入口，跨过区块边界时衰减一级。
        foreach (var seed in boundarySeeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var x = seed.X;
            var y = seed.Y;
            var z = seed.Z;
            var level = seed.Level;
            if ((uint)x >= Chunk.SizeX || !Chunk.IsValidY(y) || (uint)z >= Chunk.SizeZ ||
                level == 0 || BlockRegistry.IsOpaque(chunk.GetBlock(x, y, z).Id))
            {
                continue;
            }

            var section = chunk.GetSection(Chunk.SectionIndex(y));
            var localY = Chunk.SectionLocalY(y);
            var light = section.GetLight(x, localY, z);
            if (light.Sky >= level)
            {
                continue;
            }

            section.SetLight(x, localY, z, level, light.Block, trackDirty: false);
            queue.Enqueue((x, y, z, level));
            skySeedCount++;
        }

        Volatile.Write(ref _lastSkySeedCount, skySeedCount);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (x, y, z, currentLevel) = queue.Dequeue();
            var currentLight = chunk.GetSection(Chunk.SectionIndex(y)).GetLight(x, Chunk.SectionLocalY(y), z);
            if (currentLight.Sky != currentLevel || currentLevel <= 1)
            {
                continue;
            }

            foreach (var (dx, dy, dz) in NeighbourOffsets)
            {
                var nx = x + dx;
                var ny = y + dy;
                var nz = z + dz;
                if ((uint)nx >= Chunk.SizeX || !Chunk.IsValidY(ny) || (uint)nz >= Chunk.SizeZ)
                {
                    continue;
                }

                var neighbourBlock = chunk.GetBlock(nx, ny, nz);
                if (BlockRegistry.IsOpaque(neighbourBlock.Id))
                {
                    continue;
                }

                var loss = Math.Max(1, BlockRegistry.GetLightAttenuation(neighbourBlock.Id));
                if (currentLevel <= loss)
                {
                    continue;
                }

                var nextLevel = (byte)(currentLevel - loss);

                var neighbourSection = chunk.GetSection(Chunk.SectionIndex(ny));
                var neighbourLight = neighbourSection.GetLight(nx, Chunk.SectionLocalY(ny), nz);
                if (neighbourLight.Sky >= nextLevel)
                {
                    continue;
                }

                neighbourSection.SetLight(nx, Chunk.SectionLocalY(ny), nz, nextLevel, neighbourLight.Block, trackDirty: false);
                queue.Enqueue((nx, ny, nz, nextLevel));
            }
        }
    }

    private static (byte Sky, byte Block) GetLight(Chunk chunk, int x, int y, int z)
    {
        if (!Chunk.IsValidY(y))
        {
            return (0, 0);
        }

        return chunk.GetSection(Chunk.SectionIndex(y)).GetLight(x, Chunk.SectionLocalY(y), z);
    }

    private static SkySeedBuffer CollectBoundarySkySeeds(
        ChunkStore store,
        Chunk chunk,
        int cx,
        int cz)
    {
        var west = store.GetChunk(cx - 1, cz);
        var east = store.GetChunk(cx + 1, cz);
        var north = store.GetChunk(cx, cz - 1);
        var south = store.GetChunk(cx, cz + 1);
        var hasNeighbour = west is not null || east is not null || north is not null || south is not null;
        var seeds = new SkySeedBuffer(hasNeighbour ? MaxBoundarySeedCount : 0);
        if (west is not null)
        {
            AddWestEastSeeds(ref seeds, chunk, west, 0, Chunk.SizeX - 1);
        }

        if (east is not null)
        {
            AddWestEastSeeds(ref seeds, chunk, east, Chunk.SizeX - 1, 0);
        }

        if (north is not null)
        {
            AddNorthSouthSeeds(ref seeds, chunk, north, 0, Chunk.SizeZ - 1);
        }

        if (south is not null)
        {
            AddNorthSouthSeeds(ref seeds, chunk, south, Chunk.SizeZ - 1, 0);
        }

        return seeds;
    }

    private static void AddWestEastSeeds(
        ref SkySeedBuffer seeds,
        Chunk chunk,
        Chunk neighbour,
        int targetX,
        int neighbourX)
    {
        for (var y = Chunk.MinY; y < Chunk.MaxYExclusive; y++)
        {
            for (var z = 0; z < Chunk.SizeZ; z++)
            {
                var neighbourSky = neighbour.GetSection(Chunk.SectionIndex(y))
                    .GetLight(neighbourX, Chunk.SectionLocalY(y), z).Sky;
                AddSeed(ref seeds, chunk, targetX, y, z, neighbourSky);
            }
        }
    }

    private static void AddNorthSouthSeeds(
        ref SkySeedBuffer seeds,
        Chunk chunk,
        Chunk neighbour,
        int targetZ,
        int neighbourZ)
    {
        for (var y = Chunk.MinY; y < Chunk.MaxYExclusive; y++)
        {
            for (var x = 0; x < Chunk.SizeX; x++)
            {
                var neighbourSky = neighbour.GetSection(Chunk.SectionIndex(y))
                    .GetLight(x, Chunk.SectionLocalY(y), neighbourZ).Sky;
                AddSeed(ref seeds, chunk, x, y, targetZ, neighbourSky);
            }
        }
    }

    private static void AddSeed(
        ref SkySeedBuffer seeds,
        Chunk chunk,
        int x,
        int y,
        int z,
        byte neighbourSky)
    {
        if (neighbourSky > 1 && !BlockRegistry.IsOpaque(chunk.GetBlock(x, y, z).Id))
        {
            seeds.Add(new SkySeed(x, y, z, (byte)(neighbourSky - 1)));
        }
    }

    /// <summary>只操作脱离世界存储的区块，供 worker 阶段安全调用。</summary>
    public static void RelightChunk(Chunk chunk, CancellationToken cancellationToken)
    {
        RelightSky(chunk, cancellationToken);
        RelightBlockLight(chunk, cancellationToken);
    }

    /// <summary>重算独立的方块光通道；调用方须已完成天空光更新。</summary>
    public static void RelightBlockLight(Chunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.DirtySectionMask == AllSectionMask)
        {
            RelightBlockLightCore(chunk, cancellationToken);
            return;
        }

        Span<ulong> beforeFirst = stackalloc ulong[Chunk.SectionCount];
        Span<ulong> beforeSecond = stackalloc ulong[Chunk.SectionCount];
        CaptureLightHashes(chunk, beforeFirst, beforeSecond);
        var completed = false;
        try
        {
            RelightBlockLightCore(chunk, cancellationToken);
            completed = true;
        }
        finally
        {
            if (completed)
            {
                MarkChangedSections(chunk, beforeFirst, beforeSecond);
            }
            else
            {
                MarkAllSectionsDirty(chunk);
            }
        }
    }

    private static void RelightBlockLightCore(Chunk chunk, CancellationToken cancellationToken)
    {
        ClearBlockLight(chunk, cancellationToken);
        FloodBlockLightSources(chunk, cancellationToken);
    }

    private static void MarkAllSectionsDirty(Chunk chunk)
        => chunk.MarkAllDirtySections();

    private static void CaptureLightHashes(Chunk chunk, Span<ulong> first, Span<ulong> second)
    {
        for (var sectionIndex = 0; sectionIndex < Chunk.SectionCount; sectionIndex++)
        {
            var hash = chunk.GetSection(sectionIndex).GetLightHash();
            first[sectionIndex] = hash.A;
            second[sectionIndex] = hash.B;
        }
    }

    private static void MarkChangedSections(
        Chunk chunk,
        ReadOnlySpan<ulong> beforeFirst,
        ReadOnlySpan<ulong> beforeSecond)
    {
        for (var sectionIndex = 0; sectionIndex < Chunk.SectionCount; sectionIndex++)
        {
            var current = chunk.GetSection(sectionIndex).GetLightHash();
            if (current.A != beforeFirst[sectionIndex] || current.B != beforeSecond[sectionIndex])
            {
                chunk.GetSection(sectionIndex).MarkDirty();
            }
        }
    }

    private static void ClearBlockLight(Chunk chunk, CancellationToken cancellationToken = default)
    {
        for (var y = Chunk.MinY; y < Chunk.MaxYExclusive; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var lz = 0; lz < Chunk.SizeZ; lz++)
            {
                for (var lx = 0; lx < Chunk.SizeX; lx++)
                {
                    var section = chunk.GetSection(Chunk.SectionIndex(y));
                    var localY = Chunk.SectionLocalY(y);
                    var light = section.GetLight(lx, localY, lz);
                    section.SetLight(lx, localY, lz, light.Sky, 0, trackDirty: false);
                }
            }
        }
    }

    private static void FloodBlockLightSources(Chunk chunk, CancellationToken cancellationToken = default)
    {
        for (var y = Chunk.MinY; y < Chunk.MaxYExclusive; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var lz = 0; lz < Chunk.SizeZ; lz++)
            {
                for (var lx = 0; lx < Chunk.SizeX; lx++)
                {
                    var def = BlockRegistry.Get(chunk.GetBlock(lx, y, lz).Id);
                    if (def.LightLevel > 0)
                    {
                        FloodBlockLight(chunk, lx, y, lz, (byte)Math.Min(def.LightLevel, MaxLight), cancellationToken);
                    }
                }
            }
        }
    }

    private static void FloodBlockLight(
        Chunk chunk,
        int startX,
        int startY,
        int startZ,
        byte level,
        CancellationToken cancellationToken = default)
    {
        var queue = new Queue<(int X, int Y, int Z, byte Level)>();
        queue.Enqueue((startX, startY, startZ, level));
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (x, y, z, currentLevel) = queue.Dequeue();
            var section = chunk.GetSection(Chunk.SectionIndex(y));
            var ly = Chunk.SectionLocalY(y);
            var sky = section.GetLight(x, ly, z).Sky;
            var existing = section.GetLight(x, ly, z).Block;
            if (existing >= currentLevel)
            {
                continue;
            }

            section.SetLight(x, ly, z, sky, currentLevel, trackDirty: false);
            if (currentLevel <= 1)
            {
                continue;
            }

            var nextLevel = (byte)(currentLevel - 1);
            foreach (var (dx, dy, dz) in NeighbourOffsets)
            {
                var nx = x + dx;
                var ny = y + dy;
                var nz = z + dz;
                if ((uint)nx >= Chunk.SizeX || !Chunk.IsValidY(ny) || (uint)nz >= Chunk.SizeZ)
                {
                    continue;
                }

                if (BlockRegistry.IsOpaque(chunk.GetBlock(nx, ny, nz).Id))
                {
                    continue;
                }

                queue.Enqueue((nx, ny, nz, nextLevel));
            }
        }
    }
}
