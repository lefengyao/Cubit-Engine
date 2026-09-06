using System.Diagnostics;
using System.Numerics;

namespace Cubit.Voxel.World;

/// <summary>worker 生成和初始局部光照的阶段计时；用于可选流式诊断。</summary>
public readonly record struct GeneratedChunkTiming(
    long GenerationElapsedTicks,
    long GenerationAllocatedBytes,
    long InitialLightingElapsedTicks,
    long InitialLightingAllocatedBytes);

/// <summary>worker 生成的区块和该区块拥有的特征写入批次。</summary>
public sealed record GeneratedChunkData(
    Chunk Chunk,
    IReadOnlyList<FeaturePlacementBatch> FeatureBatches,
    GeneratedChunkTiming Timing = default);

/// <summary>带有 origin 区块所有权的特征批次；跨区块写入由主线程提交。</summary>
public sealed record FeaturePlacementBatch(
    int OriginChunkX,
    int OriginChunkZ,
    FeaturePlacementBuffer Buffer);

/// <summary>最近一次跨区块特征提交的通用工作量，供性能诊断与回归检查读取。</summary>
public readonly record struct FeaturePlacementCommitStatistics(
    int BlockWrites,
    int TargetChunks,
    int SkyRelightPasses,
    int DeferredSkyRelightTargets);

/// <summary>
/// 体素世界：管理区块生成、网格数据与方块查询/修改。
/// 线程安全：流式生成工作线程与主线程共享；所有结构操作经 Store.SyncRoot 保护。
/// </summary>
public sealed class VoxelWorld
{
    public const int GenerateRadius = 8;

    private static long _nextWorldInstanceId;
    private readonly long _worldInstanceId;
    private readonly uint _seed;
    private readonly WorldType _worldType;
    private readonly SurfaceRules _surfaceRules;
    private readonly IVoxelTerrainPass[] _terrainPasses;
    private readonly IVoxelFeaturePass[] _featurePasses;
    private readonly Dictionary<(int X, int Z), ChunkMeshLayers> _meshes = [];
    private readonly Dictionary<(int X, int Z), long> _meshRevisions = [];
    private readonly Dictionary<(int X, int Z), long> _terrainChunkRevisions = [];
    private readonly Dictionary<(int X, int Z), ulong> _terrainChangedSectionMasks = [];
    private readonly HashSet<(int X, int Z)> _dirtyChunks = [];
    private readonly Dictionary<(int X, int Z), List<FeatureBlockPlacement>> _pendingFeaturePlacements = [];
    private readonly HashSet<(int X, int Z)> _featureMeshDirtyChunks = [];
    private readonly HashSet<(int X, int Z)> _featureLightingDirtyChunks = [];
    private FeaturePlacementCommitStatistics _lastFeaturePlacementCommitStatistics;
    private readonly Queue<(int X, int Z)> _pendingMeshKeys = [];
    private readonly Queue<(int X, int Z)> _unloadedChunkKeys = [];
    private readonly Dictionary<(int X, int Z), ChunkMeshLayers> _pendingMeshes = [];
    private readonly List<ScheduledTick> _scheduledTicks = [];
    private readonly List<BlockEntity> _blockEntities = [];
    private readonly VoxelFluidSimulationOptions _fluidOptions;
    private readonly VoxelFluidSimulator? _fluidSimulator;
    private long _nextScheduledId;
    private long _terrainRevision;

    private readonly record struct ScheduledTick(int X, int Y, int Z, long DueTick, long Order);

    private struct FeaturePlacementCommitAccumulator
    {
        public int BlockWrites;
        public int TargetChunks;
        public int SkyRelightPasses;
        public int DeferredSkyRelightTargets;

        public readonly FeaturePlacementCommitStatistics ToStatistics() => new(
            BlockWrites,
            TargetChunks,
            SkyRelightPasses,
            DeferredSkyRelightTargets);
    }

    public ChunkStore Store { get; } = new();

    /// <summary>返回需要增量保存的区块坐标快照。</summary>
    public (int X, int Z)[] DirtyChunkKeys
    {
        get
        {
            lock (Store.SyncRoot)
            {
                var keys = new HashSet<(int X, int Z)>(_dirtyChunks);
                foreach (var key in Store.ChunkKeys)
                {
                    var chunk = Store.GetChunkUnsafe(key.X, key.Z);
                    if (chunk is not null && chunk.DirtySectionMask != 0)
                    {
                        keys.Add(key);
                    }
                }

                return [.. keys];
            }
        }
    }

    /// <summary>移除视距外且已完成保存的区块，并返回需要释放渲染资源的坐标。</summary>
    public (int X, int Z)[] UnloadChunksOutside(int centerCx, int centerCz, int radius)
    {
        if (radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "卸载半径不能为负数");
        }

        var unloaded = new List<(int X, int Z)>();
        foreach (var (cx, cz) in Store.ChunkKeys)
        {
            if (Math.Abs(cx - centerCx) <= radius && Math.Abs(cz - centerCz) <= radius)
            {
                continue;
            }

            if (UnloadChunk(cx, cz))
            {
                unloaded.Add((cx, cz));
            }
        }

        return [.. unloaded];
    }

    /// <summary>按观察点更新已加载区块的三层 ticket 等级。</summary>
    public void UpdateTicketLevels(int centerCx, int centerCz, ChunkTicketDistances distances)
    {
        lock (Store.SyncRoot)
        {
            foreach (var (cx, cz) in Store.ChunkKeys)
            {
                Store.SetTicketLevel(
                    cx,
                    cz,
                    distances.GetLevel(centerCx, centerCz, cx, cz));
            }
        }
    }

    /// <summary>取出已卸载区块坐标，供节点释放对应 GPU 网格 RID。</summary>
    public IReadOnlyList<(int X, int Z)> ConsumeUnloadedChunkKeys()
    {
        var result = new List<(int X, int Z)>();
        lock (Store.SyncRoot)
        {
            while (_unloadedChunkKeys.Count > 0)
            {
                result.Add(_unloadedChunkKeys.Dequeue());
            }
        }

        return result;
    }

    public uint Seed => _seed;

    public WorldType WorldType => _worldType;

    /// <summary>已完成编译的区块网格数量，供项目性能诊断读取。</summary>
    public int MeshedChunkCount
    {
        get
        {
            lock (Store.SyncRoot)
            {
                return _meshes.Count;
            }
        }
    }

    /// <summary>已编译半透明流体网格的区块数量，供项目诊断透明提交规模。</summary>
    public int FluidMeshChunkCount
    {
        get
        {
            lock (Store.SyncRoot)
            {
                return _meshes.Values.Count(mesh =>
                    mesh.Fluid.Vertices.Length > 0 && mesh.Fluid.Indices.Length > 0);
            }
        }
    }

    /// <summary>等待渲染层上传的区块网格数量。</summary>
    public int PendingMeshUploadCount
    {
        get
        {
            lock (Store.SyncRoot)
            {
                return _pendingMeshes.Count;
            }
        }
    }

    /// <summary>体素碰撞和其他派生数据用的单调地形修订号。</summary>
    public long TerrainRevision => Interlocked.Read(ref _terrainRevision);

    /// <summary>取得当前世界实例内给定区块窗口的稳定地形修订摘要，供局部派生数据跳过无关世界更新。</summary>
    public ulong GetTerrainWindowRevision(int centerChunkX, int centerChunkZ, int radius)
    {
        if (radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "区块窗口半径不能为负数");
        }

        lock (Store.SyncRoot)
        {
            var hash = 14695981039346656037UL;
            // 派生碰撞数据不能跨世界会话复用，即使两个世界的区块修订序列恰好相同。
            hash ^= unchecked((ulong)_worldInstanceId);
            hash *= 1099511628211UL;
            for (var chunkZ = centerChunkZ - radius; chunkZ <= centerChunkZ + radius; chunkZ++)
            {
                for (var chunkX = centerChunkX - radius; chunkX <= centerChunkX + radius; chunkX++)
                {
                    hash ^= unchecked((uint)chunkX);
                    hash *= 1099511628211UL;
                    hash ^= unchecked((uint)chunkZ);
                    hash *= 1099511628211UL;
                    hash ^= unchecked((ulong)_terrainChunkRevisions.GetValueOrDefault((chunkX, chunkZ)));
                    hash *= 1099511628211UL;
                }
            }

            return hash;
        }
    }

    /// <summary>取得单个区块的地形修订号，供体素碰撞等局部派生缓存定位变化区块。</summary>
    public long GetTerrainChunkRevision(int chunkX, int chunkZ)
    {
        lock (Store.SyncRoot)
        {
            return _terrainChunkRevisions.GetValueOrDefault((chunkX, chunkZ));
        }
    }

    /// <summary>取出并清除单个区块最近方块编辑涉及的 Section 位图。</summary>
    internal ulong ConsumeTerrainChangedSectionMask(int chunkX, int chunkZ)
    {
        lock (Store.SyncRoot)
        {
            var key = (chunkX, chunkZ);
            return _terrainChangedSectionMasks.Remove(key, out var mask) ? mask : 0UL;
        }
    }

    public VoxelWorld(
        uint seed,
        WorldType worldType = WorldType.Noise,
        SurfaceRules? surfaceRules = null,
        IEnumerable<IVoxelTerrainPass>? terrainPasses = null,
        IEnumerable<IVoxelFeaturePass>? featurePasses = null,
        VoxelFluidSimulationOptions? fluidOptions = null)
    {
        _worldInstanceId = Interlocked.Increment(ref _nextWorldInstanceId);
        _seed = seed;
        _worldType = worldType;
        _surfaceRules = surfaceRules ?? SurfaceRules.Default;
        _terrainPasses = terrainPasses?.Where(pass => pass is not null)
            .OrderBy(pass => pass.Id, StringComparer.Ordinal)
            .ToArray() ?? [];
        _featurePasses = featurePasses?.Where(pass => pass is not null)
            .OrderBy(pass => pass.Id, StringComparer.Ordinal)
            .ToArray() ?? [];
        _fluidOptions = fluidOptions ?? new VoxelFluidSimulationOptions();
        _fluidOptions.Validate();
        _fluidSimulator = _fluidOptions.Enabled ? new VoxelFluidSimulator(_fluidOptions) : null;
    }

    public VoxelFluidSimulationOptions FluidSimulationOptions => _fluidOptions;

    public IReadOnlyList<(int X, int Y, int Z)> FluidActivationCoordinates =>
        _fluidSimulator?.ActivationCoordinates ?? [];

    public int FluidQueueOverflowCount => _fluidSimulator?.QueueOverflowCount ?? 0;

    public void RestoreFluidActivations(IEnumerable<(int X, int Y, int Z)> coordinates)
        => _fluidSimulator?.RestoreActivations(coordinates);

    public (int X, int Z)[] ConsumeFluidMeshDirtyChunkKeys()
        => _fluidSimulator?.ConsumeDirtyChunks() ?? [];

    /// <summary>当前世界显式注入的特征通道；Voxel 不扫描项目路径。</summary>
    public IReadOnlyList<IVoxelFeaturePass> FeaturePasses => _featurePasses;

    /// <summary>当前世界显式注入的生成前地形通道；Voxel 不解释其项目语义。</summary>
    public IReadOnlyList<IVoxelTerrainPass> TerrainPasses => _terrainPasses;

    /// <summary>最近一次区块提交中跨区块特征写入的批量统计。</summary>
    public FeaturePlacementCommitStatistics LastFeaturePlacementCommitStatistics
    {
        get
        {
            lock (Store.SyncRoot)
            {
                return _lastFeaturePlacementCommitStatistics;
            }
        }
    }

    /// <summary>取出跨区块特征导致网格失效的区块，供 ChunkStreamer 分帧重建。</summary>
    public (int X, int Z)[] ConsumeFeatureMeshDirtyChunkKeys()
    {
        lock (Store.SyncRoot)
        {
            var keys = _featureMeshDirtyChunks.ToArray();
            _featureMeshDirtyChunks.Clear();
            return keys;
        }
    }

    /// <summary>取出跨区块特征的待同步光照目标；ChunkStreamer 以既有分帧步骤处理。</summary>
    internal (int X, int Z)[] ConsumeFeatureLightingDirtyChunkKeys()
    {
        lock (Store.SyncRoot)
        {
            var keys = _featureLightingDirtyChunks.ToArray();
            _featureLightingDirtyChunks.Clear();
            return keys;
        }
    }

    /// <summary>该位置最终列的最高表面上方 Y；静态流体存在时返回水面上方。</summary>
    public int GetSpawnHeight(int x, int z) => _worldType switch
    {
        WorldType.Flat => WorldGenerator.FlatLayers.Length,
        _ => GetTerrainColumn(x, z).AboveSurfaceY,
    };

    private VoxelTerrainColumn GetTerrainColumn(int x, int z) =>
        WorldGenerator.GetTerrainColumn(x, z, _seed, _worldType, _terrainPasses);

    /// <summary>同步生成指定区块（原子）。</summary>
    public void GenerateChunkAt(int cx, int cz)
    {
        var generated = CreateGeneratedChunkData(cx, cz, CancellationToken.None);
        CommitGeneratedChunkData(cx, cz, generated, synchronizeLighting: true);
    }

    /// <summary>在不触碰 Store 的情况下生成并点亮独立区块。</summary>
    public Chunk? CreateGeneratedChunk(int cx, int cz, CancellationToken cancellationToken)
        => CreateGeneratedChunkData(cx, cz, cancellationToken).Chunk;

    /// <summary>在不触碰 Store 的情况下生成区块及其跨区块特征批次。</summary>
    public GeneratedChunkData CreateGeneratedChunkData(
        int cx,
        int cz,
        CancellationToken cancellationToken,
        bool captureTiming = false)
    {
        var generationStarted = captureTiming ? Stopwatch.GetTimestamp() : 0L;
        var generationAllocationStarted = captureTiming ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        var chunk = new Chunk();
        var featureBatches = new List<FeaturePlacementBatch>();
        if (_worldType == WorldType.Flat)
        {
            WorldGenerator.GenerateFlatChunk(chunk, cancellationToken);
        }
        else
        {
            var terrainCache = new Dictionary<(int X, int Z), VoxelTerrainColumn>(Chunk.SizeX * Chunk.SizeZ);
            WorldGenerator.GenerateChunk(
                chunk,
                cx,
                cz,
                _seed,
                _surfaceRules,
                _terrainPasses,
                cancellationToken,
                terrainCache);
            featureBatches.AddRange(GenerateFeatures(chunk, cx, cz, cancellationToken, terrainCache));
        }

        var generationElapsedTicks = captureTiming ? Stopwatch.GetTimestamp() - generationStarted : 0L;
        var generationAllocatedBytes = captureTiming
            ? GC.GetAllocatedBytesForCurrentThread() - generationAllocationStarted
            : 0L;
        var lightingStarted = captureTiming ? Stopwatch.GetTimestamp() : 0L;
        var lightingAllocationStarted = captureTiming ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        LightingEngine.RelightChunk(chunk, cancellationToken);
        var lightingElapsedTicks = captureTiming ? Stopwatch.GetTimestamp() - lightingStarted : 0L;
        var lightingAllocatedBytes = captureTiming
            ? GC.GetAllocatedBytesForCurrentThread() - lightingAllocationStarted
            : 0L;
        // 新生成区块首次保存需要完整负载；后续加载区块再使用精细 Section dirty 掩码。
        chunk.MarkAllDirtySections();
        return new GeneratedChunkData(
            chunk,
            featureBatches,
            new GeneratedChunkTiming(
                generationElapsedTicks,
                generationAllocatedBytes,
                lightingElapsedTicks,
                lightingAllocatedBytes));
    }

    private IReadOnlyList<FeaturePlacementBatch> GenerateFeatures(
        Chunk chunk,
        int chunkX,
        int chunkZ,
        CancellationToken cancellationToken,
        IDictionary<(int X, int Z), VoxelTerrainColumn>? terrainCache = null)
    {
        if (_featurePasses.Length == 0)
        {
            return [];
        }

        terrainCache ??= new Dictionary<(int X, int Z), VoxelTerrainColumn>();

        VoxelTerrainColumn GetCachedTerrainColumn(int x, int z)
        {
            var key = (x, z);
            if (terrainCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var generated = GetTerrainColumn(x, z);
            terrainCache.Add(key, generated);
            return generated;
        }

        BlockState GetCachedGeneratedBlock(int x, int y, int z) =>
            WorldGenerator.GetColumnBlock(GetCachedTerrainColumn(x, z), y, _surfaceRules);

        var originX = chunkX * Chunk.SizeX + Chunk.SizeX / 2;
        var originZ = chunkZ * Chunk.SizeZ + Chunk.SizeZ / 2;
        var originY = GetCachedTerrainColumn(originX, originZ).AboveSurfaceY;
        var context = new ChunkGenerationContext(
            chunkX,
            chunkZ,
            _seed,
            (x, y, z) =>
            {
                if (!Chunk.IsValidY(y))
                {
                    return false;
                }

                var targetChunkX = Math.DivRem(x, Chunk.SizeX, out var localX);
                var targetChunkZ = Math.DivRem(z, Chunk.SizeZ, out var localZ);
                if (localX < 0)
                {
                    targetChunkX--;
                    localX += Chunk.SizeX;
                }

                if (localZ < 0)
                {
                    targetChunkZ--;
                    localZ += Chunk.SizeZ;
                }

                return targetChunkX == chunkX && targetChunkZ == chunkZ
                    ? chunk.GetBlock(localX, y, localZ) == BlockState.Air
                    : GetCachedGeneratedBlock(x, y, z).IsAir;
            },
            _worldType,
            (x, z) => GetCachedTerrainColumn(x, z).AboveSurfaceY,
            (x, y, z) =>
            {
                var targetChunkX = Math.DivRem(x, Chunk.SizeX, out var localX);
                var targetChunkZ = Math.DivRem(z, Chunk.SizeZ, out var localZ);
                if (localX < 0)
                {
                    targetChunkX--;
                    localX += Chunk.SizeX;
                }

                if (localZ < 0)
                {
                    targetChunkZ--;
                    localZ += Chunk.SizeZ;
                }

                return targetChunkX == chunkX && targetChunkZ == chunkZ && Chunk.IsValidY(y)
                    ? chunk.GetBlock(localX, y, localZ)
                    : GetCachedGeneratedBlock(x, y, z);
            });

        var batches = new List<FeaturePlacementBatch>(_featurePasses.Length);
        foreach (var pass in _featurePasses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var buffer = pass.Generate(in context, originX, originY, originZ, _seed);
            ApplyFeatureBufferToChunk(chunk, chunkX, chunkZ, buffer);
            if (!buffer.IsEmpty)
            {
                batches.Add(new FeaturePlacementBatch(chunkX, chunkZ, buffer));
            }
        }

        return batches;
    }

    private static void ApplyFeatureBufferToChunk(
        Chunk chunk,
        int chunkX,
        int chunkZ,
        FeaturePlacementBuffer buffer)
    {
        foreach (var placement in buffer.Placements)
        {
            var targetChunkX = Math.DivRem(placement.X, Chunk.SizeX, out var localX);
            var targetChunkZ = Math.DivRem(placement.Z, Chunk.SizeZ, out var localZ);
            if (localX < 0)
            {
                targetChunkX--;
                localX += Chunk.SizeX;
            }

            if (localZ < 0)
            {
                targetChunkZ--;
                localZ += Chunk.SizeZ;
            }

            if (targetChunkX != chunkX || targetChunkZ != chunkZ || !Chunk.IsValidY(placement.Y))
            {
                continue;
            }

            if (chunk.GetBlock(localX, placement.Y, localZ) != BlockState.Air)
            {
                continue;
            }

            var blockId = BlockRegistry.GetId(placement.BlockKey);
            chunk.SetBlock(localX, placement.Y, localZ, BlockRegistry.GetState(blockId));
        }
    }

    /// <summary>主线程按确定顺序把独立区块短暂提交到 Store。</summary>
    public bool CommitGeneratedChunk(int cx, int cz, Chunk chunk)
        => CommitGeneratedChunkData(
            cx,
            cz,
            new GeneratedChunkData(chunk, []),
            synchronizeLighting: true);

    /// <summary>提交独立生成区块但延后邻区天空光同步，供流式器按帧预算分摊。</summary>
    internal bool CommitGeneratedChunkDeferredLighting(int cx, int cz, Chunk chunk)
        => CommitGeneratedChunkData(
            cx,
            cz,
            new GeneratedChunkData(chunk, []),
            synchronizeLighting: false);

    internal bool CommitGeneratedChunkDeferredLighting(
        int cx,
        int cz,
        Chunk chunk,
        IReadOnlyList<FeaturePlacementBatch>? featureBatches)
        => CommitGeneratedChunkData(
            cx,
            cz,
            new GeneratedChunkData(chunk, featureBatches ?? []),
            synchronizeLighting: false);

    private bool CommitGeneratedChunkData(
        int cx,
        int cz,
        GeneratedChunkData generated,
        bool synchronizeLighting)
    {
        ArgumentNullException.ThrowIfNull(generated);
        var chunk = generated.Chunk;
        ArgumentNullException.ThrowIfNull(chunk);
        lock (Store.SyncRoot)
        {
            if (Store.HasChunk(cx, cz))
            {
                return false;
            }

            var featureCommit = default(FeaturePlacementCommitAccumulator);
            Store.AddChunk(cx, cz, chunk);
            ApplyPendingFeaturePlacementsLocked(cx, cz, chunk, synchronizeLighting, ref featureCommit);
            QueueOrApplyFeatureBatchesLocked(generated.FeatureBatches, synchronizeLighting, ref featureCommit);
            _lastFeaturePlacementCommitStatistics = featureCommit.ToStatistics();
            // 在任何可能取消/抛错的光照同步前登记新生成区块，避免异常留下不可保存对象。
            _dirtyChunks.Add((cx, cz));
            Store.SetStatus(cx, cz, ChunkStatus.Generated);
            Store.SetStatus(cx, cz, ChunkStatus.Lighted);
            if (synchronizeLighting)
            {
                LightingEngine.RelightSkyAfterChunkAdded(Store, cx, cz);
            }

            MarkDirtyChunksWithChangedSectionsLocked(cx, cz);
            MarkTerrainChanged(cx, cz);
            _fluidSimulator?.SeedChunk(this, cx, cz);
            return true;
        }
    }

    /// <summary>推进一个新区块天空光同步步骤，并保留 Section dirty 结果。</summary>
    internal bool SynchronizeGeneratedChunkLightingStep(int cx, int cz, int step)
    {
        lock (Store.SyncRoot)
        {
            var completed = LightingEngine.RelightSkyAfterChunkAddedStep(Store, cx, cz, step);
            if (Store.HasChunk(cx, cz))
            {
                MarkDirtyChunksWithChangedSectionsLocked(cx, cz);
            }

            return completed;
        }
    }

    /// <summary>主线程按确定顺序提交从 region 读取的区块，不重复生成或重算已保存光照。</summary>
    public bool CommitLoadedChunk(int cx, int cz, PersistedChunk persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted.Chunk);
        lock (Store.SyncRoot)
        {
            if (Store.HasChunk(cx, cz))
            {
                return false;
            }

            var featureCommit = default(FeaturePlacementCommitAccumulator);
            Store.AddChunk(cx, cz, persisted.Chunk);
            ApplyPendingFeaturePlacementsLocked(cx, cz, persisted.Chunk, synchronizeLighting: false, ref featureCommit);
            _lastFeaturePlacementCommitStatistics = featureCommit.ToStatistics();
            // 存档中的 Meshed/Ready 只描述上次会话；当前会话仍必须重新快照、编译、
            // 发布网格后才能提升状态，避免加载进度在 GPU 尚未提交时提前完成。
            // 存档中的 Meshed/Ready 只描述上次会话；当前会话仍必须重新快照、
            // 编译、发布网格后才能提升状态，避免加载进度在 GPU 尚未提交时提前完成。
            var loadedStatus = persisted.Status >= ChunkStatus.Meshed
                ? ChunkStatus.Lighted
                : persisted.Status;
            Store.SetStatus(cx, cz, loadedStatus);
            Store.SetTicketLevel(cx, cz, persisted.TicketLevel);
            MarkTerrainChanged(cx, cz);
            _fluidSimulator?.SeedChunk(this, cx, cz);
            return true;
        }
    }

    private void QueueOrApplyFeatureBatchesLocked(
        IReadOnlyList<FeaturePlacementBatch> batches,
        bool synchronizeLighting,
        ref FeaturePlacementCommitAccumulator featureCommit)
    {
        var loadedTargets = new Dictionary<(int X, int Z), List<FeatureBlockPlacement>>();
        foreach (var batch in batches)
        {
            foreach (var placement in batch.Buffer.Placements)
            {
                var target = ChunkStore.WorldToChunk(placement.X, placement.Z);
                if (target == (batch.OriginChunkX, batch.OriginChunkZ))
                {
                    continue;
                }

                if (Store.GetChunkUnsafe(target.X, target.Z) is not null)
                {
                    if (!loadedTargets.TryGetValue(target, out var loaded))
                    {
                        loaded = [];
                        loadedTargets.Add(target, loaded);
                    }

                    loaded.Add(placement);
                }
                else
                {
                    if (!_pendingFeaturePlacements.TryGetValue(target, out var pending))
                    {
                        pending = [];
                        _pendingFeaturePlacements.Add(target, pending);
                    }

                    pending.Add(placement);
                }
            }
        }

        foreach (var (target, placements) in loadedTargets.OrderBy(item => item.Key.X).ThenBy(item => item.Key.Z))
        {
            var targetChunk = Store.GetChunkUnsafe(target.X, target.Z)
                ?? throw new InvalidOperationException("跨区块特征目标区块在提交期间被移除");
            ApplyFeaturePlacementsLocked(
                target.X,
                target.Z,
                targetChunk,
                placements,
                synchronizeLighting,
                ref featureCommit);
        }
    }

    private void ApplyPendingFeaturePlacementsLocked(
        int chunkX,
        int chunkZ,
        Chunk chunk,
        bool synchronizeLighting,
        ref FeaturePlacementCommitAccumulator featureCommit)
    {
        var key = (chunkX, chunkZ);
        if (!_pendingFeaturePlacements.Remove(key, out var pending))
        {
            return;
        }

        ApplyFeaturePlacementsLocked(
            chunkX,
            chunkZ,
            chunk,
            pending,
            synchronizeLighting,
            ref featureCommit);
    }

    private void ApplyFeaturePlacementsLocked(
        int chunkX,
        int chunkZ,
        Chunk chunk,
        IEnumerable<FeatureBlockPlacement> placements,
        bool synchronizeLighting,
        ref FeaturePlacementCommitAccumulator featureCommit)
    {
        var writes = 0;
        var changedSectionMask = 0UL;
        var relightBlockLight = false;
        var touchesWest = false;
        var touchesEast = false;
        var touchesNorth = false;
        var touchesSouth = false;
        foreach (var placement in placements
                     .OrderBy(item => item.X)
                     .ThenBy(item => item.Y)
                     .ThenBy(item => item.Z)
                     .ThenBy(item => item.BlockKey, StringComparer.Ordinal))
        {
            if (!Chunk.IsValidY(placement.Y))
            {
                continue;
            }

            var (localX, localY, localZ) = ChunkStore.WorldToLocal(placement.X, placement.Y, placement.Z);
            if (chunk.GetBlock(localX, localY, localZ) != BlockState.Air)
            {
                continue;
            }

            var blockId = BlockRegistry.GetId(placement.BlockKey);
            var next = BlockRegistry.GetState(blockId);
            chunk.SetBlock(localX, localY, localZ, next);
            writes++;
            changedSectionMask |= 1UL << Chunk.SectionIndex(placement.Y);
            relightBlockLight |= BlockRegistry.Get(blockId).LightLevel > 0;
            touchesWest |= localX == 0;
            touchesEast |= localX == Chunk.SizeX - 1;
            touchesNorth |= localZ == 0;
            touchesSouth |= localZ == Chunk.SizeZ - 1;
        }

        if (writes == 0)
        {
            return;
        }

        _dirtyChunks.Add((chunkX, chunkZ));
        MarkTerrainChanged(chunkX, chunkZ);
        var key = (chunkX, chunkZ);
        _terrainChangedSectionMasks[key] = _terrainChangedSectionMasks.GetValueOrDefault(key) | changedSectionMask;
        // 跨区块树叶等 feature 可能一次写入数十个方块；同步路径只做一次完整校正，
        // 流式路径交由 ChunkStreamer 现有的逐帧光照队列，避免提交帧尖峰。
        if (synchronizeLighting)
        {
            LightingEngine.RelightSkyAround(Store, chunkX, chunkZ);
            featureCommit.SkyRelightPasses++;
        }
        else
        {
            _featureLightingDirtyChunks.Add(key);
            featureCommit.DeferredSkyRelightTargets++;
        }
        if (relightBlockLight)
        {
            LightingEngine.RelightBlockLight(chunk);
        }

        // 流式路径在分帧光照完成后由 ChunkStreamer 重建中心和四邻区，避免先按旧光照做一次无效网格。
        if (synchronizeLighting)
        {
            _featureMeshDirtyChunks.Add((chunkX, chunkZ));
            if (touchesWest)
            {
                _featureMeshDirtyChunks.Add((chunkX - 1, chunkZ));
            }

            if (touchesEast)
            {
                _featureMeshDirtyChunks.Add((chunkX + 1, chunkZ));
            }

            if (touchesNorth)
            {
                _featureMeshDirtyChunks.Add((chunkX, chunkZ - 1));
            }

            if (touchesSouth)
            {
                _featureMeshDirtyChunks.Add((chunkX, chunkZ + 1));
            }
        }

        MarkDirtyChunksWithChangedSectionsLocked(chunkX, chunkZ);
        featureCommit.BlockWrites += writes;
        featureCommit.TargetChunks++;
    }

    public bool UnloadChunk(int cx, int cz)
    {
        lock (Store.SyncRoot)
        {
            return UnloadChunkLocked(cx, cz);
        }
    }

    /// <summary>在主线程锁内复制区块持久化快照，供后台存储任务使用。</summary>
    internal bool TryCreatePersistenceSnapshot(
        int cx,
        int cz,
        out PersistedChunk snapshot,
        out bool requiresSave)
    {
        lock (Store.SyncRoot)
        {
            var chunk = Store.GetChunkUnsafe(cx, cz);
            if (chunk is null)
            {
                snapshot = default;
                requiresSave = false;
                return false;
            }

            snapshot = new PersistedChunk(
                chunk.CloneForPersistence(),
                Store.GetStatus(cx, cz),
                Store.GetTicketLevel(cx, cz));
            requiresSave = _dirtyChunks.Contains((cx, cz)) || chunk.DirtySectionMask != 0;
            return true;
        }
    }

    /// <summary>
    /// 只有后台保存的快照仍等于 live 区块时才清脏并卸载。
    /// 保存期间发生编辑时保留 live 区块，以便流式器重新复制并写入新快照。
    /// </summary>
    internal bool TryUnloadPersistedSnapshot(int cx, int cz, PersistedChunk snapshot)
    {
        lock (Store.SyncRoot)
        {
            var chunk = Store.GetChunkUnsafe(cx, cz);
            if (chunk is null ||
                Store.GetStatus(cx, cz) != snapshot.Status ||
                Store.GetTicketLevel(cx, cz) != snapshot.TicketLevel ||
                !chunk.HasSamePersistenceContent(snapshot.Chunk))
            {
                return false;
            }

            _dirtyChunks.Remove((cx, cz));
            chunk.ClearDirtySections();
            return UnloadChunkLocked(cx, cz);
        }
    }

    private bool UnloadChunkLocked(int cx, int cz)
    {
        var chunk = Store.GetChunkUnsafe(cx, cz);
        if (_dirtyChunks.Contains((cx, cz)) || (chunk is not null && chunk.DirtySectionMask != 0))
        {
            return false;
        }

        if (!Store.RemoveChunk(cx, cz, out _))
        {
            return false;
        }

        var key = (cx, cz);
        _meshes.Remove(key);
        _meshRevisions.Remove(key);
        _pendingMeshes.Remove(key);
        _terrainChunkRevisions.Remove(key);
        _terrainChangedSectionMasks.Remove(key);
        _unloadedChunkKeys.Enqueue(key);
        return true;
    }

    /// <summary>
    /// 若区块自身与 4 个水平邻区都已生成，则生成网格并放入待上传队列（原子）。
    /// 网格化后还会尝试为邻区补网格。
    /// </summary>
    /// <summary>
    /// 生成区块网格并放入待上传队列（原子）。
    /// 不要求邻区齐全（缺失邻区按空气处理，边界会补面）；
    /// 邻区随后到达时由调用方对相邻区块补网格，消除边界洞。
    /// </summary>
    public bool MeshChunk(int cx, int cz)
    {
        lock (Store.SyncRoot)
        {
            if (!Store.HasChunk(cx, cz) || _meshes.ContainsKey((cx, cz)))
            {
                return false;
            }

            var work = CreateMeshSnapshot(cx, cz);
            if (work is null)
            {
                return false;
            }

            var mesh = ChunkMesher.Mesh(work.Value.Snapshot, CancellationToken.None);
            return PublishMesh(cx, cz, work.Value.Revision, mesh);
        }
    }

    /// <summary>在短锁内复制中心区块及边界，并分配单调递增 revision。</summary>
    public (ChunkMeshSnapshot Snapshot, long Revision)? CreateMeshSnapshot(int cx, int cz)
    {
        lock (Store.SyncRoot)
        {
            if (!Store.HasChunk(cx, cz))
            {
                return null;
            }

            var key = (cx, cz);
            var revision = _meshRevisions.GetValueOrDefault(key) + 1;
            _meshRevisions[key] = revision;
            return (ChunkMeshSnapshot.Capture(Store, cx, cz), revision);
        }
    }

    /// <summary>为流式 worker 创建由调用方负责 Dispose 的池化网格快照。</summary>
    internal (ChunkMeshSnapshot Snapshot, long Revision)? CreatePooledMeshSnapshot(int cx, int cz)
    {
        lock (Store.SyncRoot)
        {
            if (!Store.HasChunk(cx, cz))
            {
                return null;
            }

            var key = (cx, cz);
            var revision = _meshRevisions.GetValueOrDefault(key) + 1;
            _meshRevisions[key] = revision;
            return (ChunkMeshSnapshot.CapturePooled(Store, cx, cz), revision);
        }
    }

    /// <summary>主线程发布网格；过期 revision 不得覆盖较新的结果。</summary>
    public bool PublishMesh(
        int cx,
        int cz,
        long revision,
        ChunkMeshLayers mesh)
    {
        lock (Store.SyncRoot)
        {
            if (_meshRevisions.GetValueOrDefault((cx, cz)) != revision)
            {
                return false;
            }

            var key = (cx, cz);
            _meshes[key] = mesh;
            // 同一区块尚未上传时只保留首次排队位置，但替换为最新网格。
            // 流式期间的边界修复不会被旧网格更新淹没。
            if (!_pendingMeshes.ContainsKey(key))
            {
                _pendingMeshKeys.Enqueue(key);
            }

            _pendingMeshes[key] = mesh;
            Store.SetStatus(cx, cz, ChunkStatus.Meshed);
            // 当前区块完成网格后，中心和四个水平邻区都可能刚好满足 Ready 条件；
            // 逐一刷新，避免先完成网格的区块永远停在 Meshed。
            foreach (var (dx, dz) in new[] { (0, 0), (-1, 0), (1, 0), (0, -1), (0, 1) })
            {
                RefreshStatusLocked(cx + dx, cz + dz);
            }
            return true;
        }
    }

    /// <summary>同步生成范围内缺失区块并网格化（测试/初始区域用）。</summary>
    public void GenerateAround(int centerCx, int centerCz, int radius)
    {
        for (var dz = -radius; dz <= radius; dz++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                var cx = centerCx + dx;
                var cz = centerCz + dz;
                GenerateChunkAt(cx, cz);
            }
        }

        for (var dz = -radius; dz <= radius; dz++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                MeshChunk(centerCx + dx, centerCz + dz);
            }
        }
    }

    /// <summary>收集范围内尚未生成的区块坐标（供流式请求）。</summary>
    public (int X, int Z)[] FindMissingChunks(int centerCx, int centerCz, int radius)
    {
        var missing = new List<(int, int)>();
        lock (Store.SyncRoot)
        {
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var cx = centerCx + dx;
                    var cz = centerCz + dz;
                    if (!Store.HasChunk(cx, cz))
                    {
                        missing.Add((cx, cz));
                    }
                }
            }
        }

        return [.. missing];
    }

    /// <summary>取出并清空待上传网格（供渲染层上传 GPU）。</summary>
    public IReadOnlyList<((int X, int Z) Key, ChunkMeshLayers Mesh)> ConsumePendingMeshes(
        int maximumCount = int.MaxValue)
    {
        if (maximumCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount), "网格上传批次上限必须大于 0");
        }

        var result = new List<((int, int), ChunkMeshLayers)>();
        lock (Store.SyncRoot)
        {
            while (result.Count < maximumCount && _pendingMeshKeys.Count > 0)
            {
                var key = _pendingMeshKeys.Dequeue();
                if (_pendingMeshes.Remove(key, out var mesh))
                {
                    result.Add((key, mesh));
                }
            }
        }

        return result;
    }

    public BlockState GetBlock(int x, int y, int z) => Store.GetBlock(x, y, z);

    /// <summary>设置方块；只对已加载区块生效，并重建该区块网格（原子）。</summary>
    public bool SetBlock(int x, int y, int z, BlockState block)
    {
        var (cx, cz) = ChunkStore.WorldToChunk(x, z);
        lock (Store.SyncRoot)
        {
            if (!Store.HasChunk(cx, cz))
            {
                return false;
            }

            var previous = Store.GetBlock(x, y, z);
            if (previous == block)
            {
                return false;
            }

            Store.SetBlock(x, y, z, block);
            _dirtyChunks.Add((cx, cz));
            MarkTerrainChanged(cx, cz);
            MarkTerrainSectionChanged(cx, cz, y);
            var lx = x - cx * Chunk.SizeX;
            var lz = z - cz * Chunk.SizeZ;
            LightingEngine.RelightSkyAround(Store, cx, cz, lx, y, lz, previous, block);
            var changedChunk = Store.GetChunk(cx, cz)!;
            if (BlockRegistry.Get(previous.Id).LightLevel > 0 ||
                BlockRegistry.Get(block.Id).LightLevel > 0 ||
                LightingEngine.HasBlockLightAtOrAdjacent(changedChunk, lx, y, lz))
            {
                LightingEngine.RelightBlockLight(changedChunk);
            }

            MarkDirtyChunksWithChangedSectionsLocked(cx, cz);
            _fluidSimulator?.OnBlockChanged(this, x, y, z, previous, block);

            foreach (var (affectedX, affectedZ) in CollectMeshAffectedChunksLocked(cx, cz, lx, lz))
            {
                if (Store.HasChunk(affectedX, affectedZ))
                {
                    RemeshChunkLocked(affectedX, affectedZ);
                }
            }

            return true;
        }
    }

    /// <summary>仅修改数据和光照，返回需要由流式器重建网格的区块。</summary>
    public bool SetBlockDeferred(int x, int y, int z, BlockState block, out (int X, int Z)[] affectedChunks)
    {
        affectedChunks = [];
        if (!Chunk.IsValidY(y))
        {
            return false;
        }

        var (cx, cz) = ChunkStore.WorldToChunk(x, z);
        lock (Store.SyncRoot)
        {
            var chunk = Store.GetChunk(cx, cz);
            if (chunk is null)
            {
                return false;
            }

            var lx = x - cx * Chunk.SizeX;
            var lz = z - cz * Chunk.SizeZ;
            var previous = chunk.GetBlock(lx, y, lz);
            if (previous == block)
            {
                return false;
            }

            chunk.SetBlock(lx, y, lz, block);
            _dirtyChunks.Add((cx, cz));
            MarkTerrainChanged(cx, cz);
            MarkTerrainSectionChanged(cx, cz, y);

            // 所有编辑都要同步至四邻区的天空光边界；方块光通道按需单独重算。
            var previousEmitsLight = BlockRegistry.Get(previous.Id).LightLevel > 0;
            var nextEmitsLight = BlockRegistry.Get(block.Id).LightLevel > 0;
            LightingEngine.RelightSkyAround(Store, cx, cz, lx, y, lz, previous, block);
            if (previousEmitsLight || nextEmitsLight || LightingEngine.HasBlockLightAtOrAdjacent(chunk, lx, y, lz))
            {
                LightingEngine.RelightBlockLight(chunk);
            }

            MarkDirtyChunksWithChangedSectionsLocked(cx, cz);

            var affected = new List<(int X, int Z)>();
            foreach (var key in CollectMeshAffectedChunksLocked(cx, cz, lx, lz))
            {
                if (Store.HasChunk(key.X, key.Z))
                {
                    affected.Add(key);
                    _meshRevisions[key] = _meshRevisions.GetValueOrDefault(key) + 1;
                }
            }

            affectedChunks = [.. affected];
            _fluidSimulator?.OnBlockChanged(this, x, y, z, previous, block);
            return true;
        }
    }

    /// <summary>重建某区块网格（需已持有 Store.SyncRoot）。</summary>
    private void RemeshChunkLocked(int cx, int cz)
    {
        var work = CreateMeshSnapshot(cx, cz);
        if (work is null)
        {
            return;
        }

        var mesh = ChunkMesher.Mesh(work.Value.Snapshot, CancellationToken.None);
        PublishMesh(cx, cz, work.Value.Revision, mesh);
    }

    private void MarkTerrainChanged(int chunkX, int chunkZ)
    {
        var revision = Interlocked.Increment(ref _terrainRevision);
        _terrainChunkRevisions[(chunkX, chunkZ)] = revision;
    }

    private void MarkTerrainSectionChanged(int chunkX, int chunkZ, int y)
    {
        var key = (chunkX, chunkZ);
        _terrainChangedSectionMasks[key] = _terrainChangedSectionMasks.GetValueOrDefault(key) |
            (1UL << Chunk.SectionIndex(y));
    }

    /// <summary>方块网格脏区：内部编辑只影响中心区块，水平边界才需要相邻区块。</summary>
    private static IEnumerable<(int X, int Z)> CollectMeshAffectedChunksLocked(
        int chunkX,
        int chunkZ,
        int localX,
        int localZ)
    {
        yield return (chunkX, chunkZ);
        if (localX == 0)
        {
            yield return (chunkX - 1, chunkZ);
        }

        if (localX == Chunk.SizeX - 1)
        {
            yield return (chunkX + 1, chunkZ);
        }

        if (localZ == 0)
        {
            yield return (chunkX, chunkZ - 1);
        }

        if (localZ == Chunk.SizeZ - 1)
        {
            yield return (chunkX, chunkZ + 1);
        }
    }

    internal void ClearDirtyChunks(IEnumerable<(int X, int Z)> keys)
    {
        lock (Store.SyncRoot)
        {
            foreach (var key in keys)
            {
                _dirtyChunks.Remove(key);
                Store.GetChunkUnsafe(key.X, key.Z)?.ClearDirtySections();
            }
        }
    }

    private void MarkDirtyChunksWithChangedSectionsLocked(int centerCx, int centerCz)
    {
        foreach (var (dx, dz) in new[] { (0, 0), (-1, 0), (1, 0), (0, -1), (0, 1) })
        {
            var key = (centerCx + dx, centerCz + dz);
            var chunk = Store.GetChunkUnsafe(key.Item1, key.Item2);
            if (chunk is not null && chunk.DirtySectionMask != 0)
            {
                _dirtyChunks.Add(key);
            }
        }
    }

    /// <summary>邻区都生成后把区块提升为 Ready（需已持有 Store.SyncRoot）。</summary>
    private void RefreshStatusLocked(int cx, int cz)
    {
        if (Store.GetStatus(cx, cz) < ChunkStatus.Meshed)
        {
            return;
        }

        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            if (!Store.HasChunk(cx + dx, cz + dz))
            {
                return;
            }
        }

        Store.SetStatus(cx, cz, ChunkStatus.Ready);
    }

    /// <summary>当前模拟 tick 计数（从 0 开始，每 Tick() 递增 1）。</summary>
    public long TickCount { get; private set; }

    /// <summary>等待执行的通用与动态流体计划刻数量；未启用流体模拟时只统计通用计划刻。</summary>
    public int ScheduledTickCount => _scheduledTicks.Count + (_fluidSimulator?.ScheduledCount ?? 0);

    /// <summary>计划刻到期事件（宿主订阅实现方块行为；按到期 tick 升序、同 tick 按入队序）。</summary>
    public event Action<BlockState, int, int, int>? OnBlockScheduledTick;

    /// <summary>随机刻事件（宿主订阅实现随机行为；确定性：同种子同 tick 序列一致）。</summary>
    public event Action<BlockState, int, int, int>? OnBlockRandomTick;

    /// <summary>
    /// 推进一个模拟 tick（确定性顺序）：计划刻 → 随机刻 → BlockEntity。
    /// 由场景树固定 20Hz 驱动（VoxelWorldNode.PhysicsProcess）。
    /// </summary>
    public void Tick()
    {
        TickCount++;

        // 1) 计划刻：到期按 (DueTick, Order) 升序
        if (_scheduledTicks.Count > 0)
        {
            var due = _scheduledTicks.Where(e => e.DueTick <= TickCount)
                .OrderBy(e => e.DueTick).ThenBy(e => e.Order).ToArray();
            _scheduledTicks.RemoveAll(e => e.DueTick <= TickCount);
            foreach (var e in due)
            {
                OnBlockScheduledTick?.Invoke(GetBlock(e.X, e.Y, e.Z), e.X, e.Y, e.Z);
            }
        }

        // 动态流体严格位于主线程固定 tick 边界，避免 worker 与节点/GPU 交叉写入。
        _fluidSimulator?.Advance(this);

        // 2) 随机刻：已就绪且加载等级 ≥ BlockTicks 的区块，每区块 3×SectionCount 个
        foreach (var (cx, cz) in Store.ChunkKeys)
        {
            if (Store.GetStatus(cx, cz) < ChunkStatus.Ready || Store.GetTicketLevel(cx, cz) < TicketLevel.BlockTicks)
            {
                continue;
            }

            RandomTickChunk(cx, cz);
        }

        // 3) BlockEntity：每个模拟 tick 驱动一次
        for (var i = _blockEntities.Count - 1; i >= 0; i--)
        {
            _blockEntities[i].Tick();
        }
    }

    /// <summary>登记计划刻：delayTicks 个 tick 后触发 OnBlockScheduledTick。</summary>
    public void ScheduleTick(int x, int y, int z, int delayTicks)
    {
        if (delayTicks < 1)
        {
            delayTicks = 1;
        }

        _scheduledTicks.Add(new ScheduledTick(x, y, z, TickCount + delayTicks, _nextScheduledId++));
    }

    /// <summary>在指定位置创建方块实体（需先经 BlockEntityRegistry 注册工厂）。</summary>
    public BlockEntity? AddBlockEntity(ushort blockId, int x, int y, int z)
    {
        var entity = BlockEntityRegistry.Create(blockId);
        if (entity is null)
        {
            return null;
        }

        entity.World = this;
        entity.X = x;
        entity.Y = y;
        entity.Z = z;
        _blockEntities.Add(entity);
        return entity;
    }

    private void RandomTickChunk(int cx, int cz)
    {
        var rng = new DeterministicRandom((ulong)HashCode.Combine(_seed, cx, cz, (int)TickCount));
        var count = Chunk.SectionCount * 3;
        for (var i = 0; i < count; i++)
        {
            var x = cx * Chunk.SizeX + rng.Next(Chunk.SizeX);
            var z = cz * Chunk.SizeZ + rng.Next(Chunk.SizeZ);
            var y = Chunk.MinY + rng.Next(Chunk.SizeY);
            var block = GetBlock(x, y, z);
            if (block != BlockState.Air)
            {
                OnBlockRandomTick?.Invoke(block, x, y, z);
            }
        }
    }

    /// <summary>保存整个世界（区块+状态）到二进制文件。</summary>
    public void Save(string path) => WorldFile.Save(this, path);

    /// <summary>从二进制文件加载世界。</summary>
    public static VoxelWorld LoadFile(string path) => WorldFile.Load(path);

    /// <summary>
    /// DDA 射线-体素求交。返回 (命中方块坐标, 命中面前一格坐标)；未命中返回 null。
    /// </summary>
    public ((int X, int Y, int Z) Hit, (int X, int Y, int Z) Place)? Raycast(
        Vector3 origin,
        Vector3 direction,
        float maxDistance = 8f,
        VoxelRaycastFluidMode fluidMode = VoxelRaycastFluidMode.Any)
    {
        if (!Enum.IsDefined(fluidMode))
        {
            throw new ArgumentOutOfRangeException(nameof(fluidMode), fluidMode, "未知流体射线筛选模式");
        }

        var x = (int)MathF.Floor(origin.X);
        var y = (int)MathF.Floor(origin.Y);
        var z = (int)MathF.Floor(origin.Z);

        // 近零分量按零处理，避免浮点噪声（如 cos(-PI/2)≈-4.4e-8）导致轴对齐射线错误斜切
        const float epsilon = 1e-6f;
        var ax = MathF.Abs(direction.X);
        var ay = MathF.Abs(direction.Y);
        var az = MathF.Abs(direction.Z);

        var stepX = ax > epsilon && direction.X >= 0 ? 1 : ax > epsilon ? -1 : 0;
        var stepY = ay > epsilon && direction.Y >= 0 ? 1 : ay > epsilon ? -1 : 0;
        var stepZ = az > epsilon && direction.Z >= 0 ? 1 : az > epsilon ? -1 : 0;

        var tDeltaX = ax > epsilon ? MathF.Abs(1f / direction.X) : float.PositiveInfinity;
        var tDeltaY = ay > epsilon ? MathF.Abs(1f / direction.Y) : float.PositiveInfinity;
        var tDeltaZ = az > epsilon ? MathF.Abs(1f / direction.Z) : float.PositiveInfinity;

        var tMaxX = ax > epsilon ? ((direction.X > 0 ? x + 1f - origin.X : origin.X - x) * tDeltaX) : float.PositiveInfinity;
        var tMaxY = ay > epsilon ? ((direction.Y > 0 ? y + 1f - origin.Y : origin.Y - y) * tDeltaY) : float.PositiveInfinity;
        var tMaxZ = az > epsilon ? ((direction.Z > 0 ? z + 1f - origin.Z : origin.Z - z) * tDeltaZ) : float.PositiveInfinity;

        var last = (x, y, z);
        var t = 0f;
        while (t <= maxDistance)
        {
            var block = GetBlock(x, y, z);
            if (IsRaycastTarget(block, fluidMode, x, y, z))
            {
                return ((x, y, z), last);
            }

            last = (x, y, z);

            if (tMaxX < tMaxY && tMaxX < tMaxZ)
            {
                x += stepX;
                t = tMaxX;
                tMaxX += tDeltaX;
            }
            else if (tMaxY < tMaxZ)
            {
                y += stepY;
                t = tMaxY;
                tMaxY += tDeltaY;
            }
            else
            {
                z += stepZ;
                t = tMaxZ;
                tMaxZ += tDeltaZ;
            }
        }

        return null;
    }

    private static bool IsRaycastTarget(
        BlockState block,
        VoxelRaycastFluidMode fluidMode,
        int x,
        int y,
        int z)
    {
        if (block == BlockRegistry.Air)
        {
            return false;
        }

        if (!BlockRegistry.IsFluid(block.Id))
        {
            return true;
        }

        if (!VoxelFluidState.TryDecode(block, out var fluid))
        {
            throw new InvalidDataException(
                $"射线遇到无效流体状态: ({x},{y},{z}) {block}");
        }

        return fluidMode switch
        {
            VoxelRaycastFluidMode.None => false,
            VoxelRaycastFluidMode.SourceOnly =>
                fluid.Level == VoxelFluidState.SourceLevel && !fluid.Falling,
            VoxelRaycastFluidMode.Any => true,
            _ => throw new ArgumentOutOfRangeException(nameof(fluidMode), fluidMode, "未知流体射线筛选模式"),
        };
    }
}


