using Cubit.Core.Rendering;
using Cubit.Core.Scene;
using Cubit.Core.Jobs;
using Cubit.Voxel.Textures;
using System.Runtime.ExceptionServices;

namespace Cubit.Voxel.World;

/// <summary>
/// 体素世界节点：拥有世界数据，负责生成、网格上传与方块修改。
/// 网格通过 RenderingServer 的 RID 句柄管理（2 帧延迟销毁，避免 GPU 使用中释放）。
/// 支持场景文件序列化：Seed / WorldType / GenerateRadius 为 [Export]。
/// </summary>
public sealed class VoxelWorldNode : Node, IRenderingServerConsumer, IJobSystemConsumer
{
    private const int DefaultMaxMeshUploadsPerFrame = 1;
    private readonly Dictionary<(int X, int Z), (RID Opaque, RID Fluid)> _meshRids = [];
    private IChunkStorage? _storage;
    private bool _storageOwnedByCaller;
    private RID _textureRid;
    private RID _materialRid;
    private RID _fluidMaterialRid;

    public VoxelWorldNode()
    {
        Name = "World";
    }

    [Export("种子")]
    public uint Seed { get; set; } = 20260808;

    [Export("世界类型")]
    public WorldType WorldType { get; set; } = WorldType.Noise;

    [Export("生成半径")]
    public int GenerateRadius { get; set; } = VoxelWorld.GenerateRadius;

    [Export("方块 tick 半径")]
    public int BlockTickRadius { get; set; } = VoxelWorld.GenerateRadius;

    [Export("实体模拟半径")]
    public int EntitySimulationRadius { get; set; } = VoxelWorld.GenerateRadius;

    /// <summary>单帧最多提交多少个完整区块网格到 GPU，防止流式完成时卡住渲染线程。</summary>
    [Export("每帧网格上传上限")]
    public int MaxMeshUploadsPerFrame { get; set; } = DefaultMaxMeshUploadsPerFrame;

    /// <summary>由项目运行时显式注入的体素特征；节点本身不扫描项目内容。</summary>
    public IReadOnlyList<IVoxelFeaturePass> FeaturePasses { get; set; } = [];

    /// <summary>由项目运行时显式注入的生成前地形通道；节点不认识项目地貌语义。</summary>
    public IReadOnlyList<IVoxelTerrainPass> TerrainPasses { get; set; } = [];

    /// <summary>渲染服务器（非导出，由宿主注入）。</summary>
    public RenderingServer? Server { get; set; }

    /// <summary>宿主注入的通用 worker 池。</summary>
    public JobSystem? Jobs { get; set; }

    /// <summary>可选流式诊断接收器；由宿主注入，不参与作者场景序列化。</summary>
    public IChunkStreamingDiagnosticsSink? DiagnosticsSink { get; set; }

    public VoxelWorld? World { get; private set; }

    public ChunkStreamer? Streamer { get; private set; }

    /// <summary>报告作者指定焦点周围的体素区块是否已完成加载和网格化，供项目加载流程等待碰撞窗口。</summary>
    public bool IsSpawnCollisionReady(int chunkX, int chunkZ, int radius = 1)
    {
        if (radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "碰撞窗口半径不能为负数");
        }

        return Streamer?.GetAreaProgress(chunkX, chunkZ, radius).IsReady == true;
    }

    /// <summary>最近一次待上传网格处理耗时，供项目性能诊断读取。</summary>
    public double LastMeshUploadMilliseconds { get; private set; }

    protected override void Ready()
    {
        if (Jobs is null)
        {
            throw new InvalidOperationException("VoxelWorldNode 未注入 JobSystem");
        }

        if (Server is null)
        {
            throw new InvalidOperationException("VoxelWorldNode 未注入渲染服务器");
        }

        var atlasData = TextureAtlas.AtlasData;
        _textureRid = Server.CreateTexture(new TextureData(
            TextureAtlas.AtlasWidth,
            TextureAtlas.AtlasHeight,
            atlasData,
            TextureFilter.Nearest));
        _materialRid = Server.CreateMaterial(new MaterialData(_textureRid));
        _fluidMaterialRid = Server.CreateMaterial(new MaterialData(_textureRid, MaterialBlendMode.AlphaBlend));

        StartWorld(new VoxelWorld(
            Seed,
            WorldType,
            terrainPasses: TerrainPasses,
            featurePasses: FeaturePasses));
    }

    /// <summary>确保玩家周围区块已生成并上传（异步流式）。</summary>
    public void EnsureAround(int chunkX, int chunkZ)
    {
        var blockTickRadius = Math.Clamp(BlockTickRadius, 0, GenerateRadius);
        var entitySimulationRadius = Math.Clamp(EntitySimulationRadius, 0, blockTickRadius);
        Streamer?.RequestArea(
            chunkX,
            chunkZ,
            new ChunkTicketDistances(GenerateRadius, blockTickRadius, entitySimulationRadius));
    }

    protected override void Process(double delta)
    {
        Streamer?.Pump();
        if (World is not null && Server is not null)
        {
            foreach (var (chunkX, chunkZ) in World.ConsumeFluidMeshDirtyChunkKeys())
            {
                Streamer?.QueueMeshEditRequest(chunkX, chunkZ);
            }

            foreach (var key in World.ConsumeUnloadedChunkKeys())
            {
                if (_meshRids.Remove(key, out var rids))
                {
                    DestroyMeshLayers(rids);
                }
            }
        }

        // 每帧从流队列取已完成网格上传 GPU
        UploadPending();
    }

    protected override void PhysicsProcess(double delta)
    {
        // 固定模拟 tick（默认 20Hz）：推进计划刻/随机刻/BlockEntity
        World?.Tick();
    }

    protected override void ExitTree()
    {
        Exception? streamerFailure = null;
        try
        {
            Streamer?.Dispose();
        }
        catch (Exception ex)
        {
            streamerFailure = ex;
        }
        finally
        {
            Streamer = null;

            if (!_storageOwnedByCaller)
            {
                try
                {
                    (_storage as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    streamerFailure ??= ex;
                }
            }

            _storage = null;
            _storageOwnedByCaller = false;

            foreach (var rids in _meshRids.Values)
            {
                DestroyMeshLayers(rids);
            }

            _meshRids.Clear();
            if (_materialRid.IsValid)
            {
                Server?.DestroyMaterial(_materialRid);
                _materialRid = RID.None;
            }

            if (_fluidMaterialRid.IsValid)
            {
                Server?.DestroyMaterial(_fluidMaterialRid);
                _fluidMaterialRid = RID.None;
            }

            if (_textureRid.IsValid)
            {
                Server?.DestroyTexture(_textureRid);
                _textureRid = RID.None;
            }
        }

        if (streamerFailure is not null)
        {
            ExceptionDispatchInfo.Capture(streamerFailure).Throw();
        }
    }

    /// <summary>修改方块（挖/放），并重建受影响区块网格。</summary>
    public bool TrySetBlock(int x, int y, int z, ushort block)
        => TrySetBlock(x, y, z, BlockRegistry.GetState(block));

    /// <summary>修改完整方块状态（挖/放），并重建受影响区块网格。</summary>
    /// <remarks>调用方负责解释状态数据的玩法含义；节点只保留已给定的通用 BlockState。</remarks>
    public bool TrySetBlock(int x, int y, int z, BlockState block)
    {
        if (World is null)
        {
            return false;
        }

        if (!World.SetBlockDeferred(x, y, z, block, out var affectedChunks))
        {
            return false;
        }

        foreach (var (chunkX, chunkZ) in affectedChunks)
        {
            Streamer?.QueueMeshEditRequest(chunkX, chunkZ);
        }

        return true;
    }

    /// <summary>在主线程切换已加载的单人世界，并重新建立该世界的异步流式会话。</summary>
    public void ReplaceWorld(
        VoxelWorld world,
        IChunkStorage? storage = null,
        bool storageOwnedByCaller = false)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (Jobs is null || Server is null)
        {
            throw new InvalidOperationException("VoxelWorldNode 尚未完成服务注入，不能切换世界");
        }

        Streamer?.Dispose();
        Streamer = null;
        if (!_storageOwnedByCaller)
        {
            (_storage as IDisposable)?.Dispose();
        }
        _storage = null;
        _storageOwnedByCaller = false;
        foreach (var rids in _meshRids.Values)
        {
            DestroyMeshLayers(rids);
        }

        _meshRids.Clear();
        StartWorld(world, storage, storageOwnedByCaller);
    }

    /// <summary>停止当前世界的后台生成与网格流式；世界数据保留给项目存档逻辑读取。</summary>
    public void SuspendStreaming()
    {
        Exception? failure = null;
        try
        {
            Streamer?.Dispose();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            Streamer = null;
            if (!_storageOwnedByCaller)
            {
                try
                {
                    (_storage as IDisposable)?.Dispose();
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }

            _storage = null;
            _storageOwnedByCaller = false;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>保存前停止后台任务但保留存储句柄，供项目完成增量写入。</summary>
    public void PrepareForSave()
    {
        Streamer?.Dispose();
        Streamer = null;
    }

    private void StartWorld(
        VoxelWorld world,
        IChunkStorage? storage = null,
        bool storageOwnedByCaller = false)
    {
        World = world;
        _storage = storage;
        _storageOwnedByCaller = storageOwnedByCaller;
        // 由玩家或项目作者显式请求首个范围；节点入树不能同步生成整片视距并阻塞首帧。
        Streamer = new ChunkStreamer(world, Jobs
            ?? throw new InvalidOperationException("VoxelWorldNode 未注入 JobSystem"),
            storage,
            DiagnosticsSink);
    }

    private void UploadPending()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
        if (World is null || Server is null)
        {
            return;
        }

        var uploadLimit = Math.Max(1, MaxMeshUploadsPerFrame);
        foreach (var (key, mesh) in World.ConsumePendingMeshes(uploadLimit))
        {
            var diagnostics = DiagnosticsSink;
            var uploadStarted = diagnostics is not null
                ? System.Diagnostics.Stopwatch.GetTimestamp()
                : 0L;
            var uploadAllocationStarted = diagnostics is not null
                ? GC.GetAllocatedBytesForCurrentThread()
                : 0L;
            if (_meshRids.Remove(key, out var old))
            {
                DestroyMeshLayers(old);
            }

            var opaqueMesh = mesh.Opaque;
            var translation = new System.Numerics.Vector3(key.X * Chunk.SizeX, 0f, key.Z * Chunk.SizeZ);
            var boundsMin = new System.Numerics.Vector3(translation.X, Chunk.MinY, translation.Z);
            var boundsMax = new System.Numerics.Vector3(
                translation.X + Chunk.SizeX,
                Chunk.MaxYExclusive,
                translation.Z + Chunk.SizeZ);
            var rids = (Opaque: RID.None, Fluid: RID.None);
            if (opaqueMesh.Vertices.Length > 0 && opaqueMesh.Indices.Length > 0)
            {
                rids.Opaque = Server.CreateMesh(new MeshData(
                    opaqueMesh.Vertices,
                    opaqueMesh.Indices,
                    translation,
                    boundsMin,
                    boundsMax,
                    _materialRid,
                    System.Numerics.Vector4.One,
                    MeshVertexLayout.PositionUvShadeTile));
            }

            var fluidMesh = mesh.Fluid;
            if (fluidMesh.Vertices.Length > 0 && fluidMesh.Indices.Length > 0)
            {
                rids.Fluid = Server.CreateMesh(new MeshData(
                    fluidMesh.Vertices,
                    fluidMesh.Indices,
                    translation,
                    boundsMin,
                    boundsMax,
                    _fluidMaterialRid,
                    System.Numerics.Vector4.One,
                    MeshVertexLayout.PositionUvShadeTile));
            }

            if (rids.Opaque.IsValid || rids.Fluid.IsValid)
            {
                _meshRids.Add(key, rids);
            }

            if (diagnostics is not null)
            {
                diagnostics.Report(new ChunkStreamingDiagnosticSample(
                    ChunkStreamingStage.MeshUpload,
                    key.X,
                    key.Z,
                    Sequence: 0,
                    System.Diagnostics.Stopwatch.GetTimestamp() - uploadStarted,
                    GC.GetAllocatedBytesForCurrentThread() - uploadAllocationStarted,
                    LoadedFromStorage: false,
                    Frame: 0));
            }
        }
        }
        finally
        {
            LastMeshUploadMilliseconds =
                (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    private void DestroyMeshLayers((RID Opaque, RID Fluid) rids)
    {
        if (rids.Opaque.IsValid)
        {
            Server?.DestroyMesh(rids.Opaque);
        }

        if (rids.Fluid.IsValid)
        {
            Server?.DestroyMesh(rids.Fluid);
        }
    }
}
