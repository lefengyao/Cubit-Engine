using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Cubit.Core.Jobs;

namespace Cubit.Voxel.World;

/// <summary>主线程读取的活动区块范围快照；不包含项目、UI 或玩家语义。</summary>
public readonly record struct ChunkStreamingProgress(
    bool HasActiveArea,
    int CenterX,
    int CenterZ,
    int Radius,
    int TotalChunks,
    int LoadedChunks,
    int MeshedChunks,
    int PendingCount)
{
    public bool IsReady => HasActiveArea && TotalChunks > 0 &&
        LoadedChunks >= TotalChunks && MeshedChunks >= TotalChunks;
}

/// <summary>
/// 体素分阶段流水线协调器：生成和不可变快照后的网格编译进入宿主 JobSystem，
/// 快照、版本提交和发布保持主线程控制。
/// </summary>
public sealed class ChunkStreamer : IDisposable
{
    private readonly record struct GenerationRequest(
        long Sequence,
        int X,
        int Z,
        long EnqueuedAtTicks,
        long RequestedFrame);
    private readonly record struct GenerationResult(
        long Sequence,
        int X,
        int Z,
        Chunk? Chunk,
        PersistedChunk? Persisted,
        IReadOnlyList<FeaturePlacementBatch>? FeatureBatches);
    private sealed record GenerationJob(GenerationRequest Request, JobHandle Handle);
    private readonly record struct MeshResult(
        long Sequence,
        int X,
        int Z,
        long Revision,
        ChunkMeshLayers Mesh,
        long RequestedFrame);
    private sealed record MeshJob((int X, int Z) Key, JobHandle Handle);
    private sealed record UnloadSaveJob((int X, int Z) Key, PersistedChunk Snapshot, JobHandle Handle);
    private sealed record MeshQueueEntry(bool IsBoundaryRepair, LinkedListNode<(int X, int Z)> Node);
    private readonly record struct GeneratedLightingWork(int X, int Z, int Step);

    // 生成结果的提交、邻区天空光步骤和网格发布都必须分摊到渲染帧。
    // 生成提交在时间预算内可合并邻近的已完成结果，避免高视距下 worker 长时间空转。
    private const int MaxGenerationCommitsPerPump = 4;
    private const double MainThreadCommitBudgetMilliseconds = 8d;
    private const int MaxMeshSnapshotsPerPump = 1;
    // 已完成网格也必须分摊到渲染帧；否则 worker 同时完成多个网格时会在一次 Pump 中集中发布。
    private const int MaxMeshPublishesPerPump = 1;
    // Region 写入会占用存储后端和完整区块快照；慢盘时必须背压，不能随帧数无限积压。
    private const int MaxInFlightUnloadSaves = 1;
    private const long EditMeshQuietFrames = 4;
    private readonly VoxelWorld _world;
    private readonly JobSystem _jobs;
    private readonly IChunkStorage? _storage;
    private readonly IChunkStreamingDiagnosticsSink? _diagnostics;
    private readonly int _ownerThreadId;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Queue<GenerationRequest> _generationQueue = new();
    private readonly HashSet<(int X, int Z)> _generationQueued = [];
    private readonly Dictionary<long, GenerationJob> _generationJobs = [];
    private readonly SortedDictionary<long, GenerationResult> _generationResults = [];
    private readonly HashSet<long> _canceledGenerationSequences = [];
    private readonly ConcurrentQueue<GenerationResult> _completedGenerations = new();
    private readonly Dictionary<long, MeshJob> _meshJobs = [];
    private readonly HashSet<(int X, int Z)> _meshInFlight = [];
    private readonly HashSet<long> _canceledMeshSequences = [];
    private readonly ConcurrentQueue<MeshResult> _completedMeshes = new();
    private long _nextSequence = 1;
    private long _nextCommitSequence = 1;
    private long _nextMeshSequence = 1;

    private readonly LinkedList<(int X, int Z)> _boundaryRepairQueue = [];
    private readonly LinkedList<(int X, int Z)> _meshQueue = [];
    private readonly Dictionary<(int X, int Z), MeshQueueEntry> _meshQueued = [];
    private readonly HashSet<(int X, int Z)> _pendingEditMeshKeys = [];
    private readonly Dictionary<(int X, int Z), long> _lastEditFrames = [];
    private readonly HashSet<(int X, int Z)> _finalMeshTargets = [];
    private readonly Queue<GeneratedLightingWork> _generatedLightingQueue = new();
    private readonly HashSet<(int X, int Z)> _generatedLightingQueued = [];
    private readonly HashSet<(int X, int Z)> _generatedLightingRequeue = [];
    private readonly Queue<(int X, int Z)> _unloadQueue = new();
    private readonly HashSet<(int X, int Z)> _unloadQueued = [];
    private readonly Dictionary<(int X, int Z), UnloadSaveJob> _unloadSaveJobs = [];
    private int _activeCenterX;
    private int _activeCenterZ;
    private int _activeRadius;
    private ChunkTicketDistances _activeDistances;
    private bool _hasActiveArea;
    private int _meshedCount;
    private int _loadedCount;
    private int _generatedCount;
    private int _canceledGenerationCount;
    private int _canceledMeshCount;
    private int _lastPumpThreadId;
    private int _lastMeshThreadId;
    private long _generationWorkerElapsedTicks;
    private long _generationWorkerAllocatedBytes;
    private long _generationCommitElapsedTicks;
    private long _generationCommitAllocatedBytes;
    private long _meshSnapshotElapsedTicks;
    private long _meshSnapshotAllocatedBytes;
    private long _meshBuildElapsedTicks;
    private long _meshBuildAllocatedBytes;
    private long _unloadSaveElapsedTicks;
    private long _unloadSaveAllocatedBytes;
    private int _completedUnloadSaveCount;
    private int _completedUnloadCount;
    private int _unloadSaveFailureCount;
    private string? _lastUnloadSaveFailure;
    private int _lastGenerationCommitCount;
    private long _pumpFrame;
    private double _lastPumpMilliseconds;
    private bool _disposed;

    public ChunkStreamer(
        VoxelWorld world,
        JobSystem jobs,
        IChunkStorage? storage = null,
        IChunkStreamingDiagnosticsSink? diagnostics = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _storage = storage;
        _diagnostics = diagnostics;
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    public int MeshedCount => Volatile.Read(ref _meshedCount);

    public int LoadedCount => Volatile.Read(ref _loadedCount);

    public int GeneratedCount => Volatile.Read(ref _generatedCount);

    public int CanceledGenerationCount => Volatile.Read(ref _canceledGenerationCount);

    /// <summary>因观察范围变化而取消的网格 Job 数量，用于流式生命周期诊断。</summary>
    public int CanceledMeshCount => Volatile.Read(ref _canceledMeshCount);

    /// <summary>最近一次 Pump 调用所在的线程，仅用于流水线边界诊断。</summary>
    public int LastPumpThreadId => Volatile.Read(ref _lastPumpThreadId);

    /// <summary>最近一次网格化所在的线程，仅用于流水线边界诊断。</summary>
    public int LastMeshThreadId => Volatile.Read(ref _lastMeshThreadId);

    /// <summary>worker 执行区块生成和局部光照的累计耗时，单位毫秒。</summary>
    public double GenerationWorkerMilliseconds => ToMilliseconds(Interlocked.Read(ref _generationWorkerElapsedTicks));

    /// <summary>worker 执行区块生成和局部光照的累计托管分配量。</summary>
    public long GenerationWorkerAllocatedBytes => Interlocked.Read(ref _generationWorkerAllocatedBytes);

    /// <summary>主线程提交生成结果及邻区同步的累计托管分配量。</summary>
    public long GenerationCommitAllocatedBytes => Interlocked.Read(ref _generationCommitAllocatedBytes);

    /// <summary>主线程提交生成结果（含相邻天空光边界同步）的累计耗时，单位毫秒。</summary>
    public double GenerationCommitMilliseconds => ToMilliseconds(Interlocked.Read(ref _generationCommitElapsedTicks));

    /// <summary>最近一次 Pump 实际提交的生成结果数量，用于帧预算诊断。</summary>
    public int LastGenerationCommitCount => Volatile.Read(ref _lastGenerationCommitCount);

    /// <summary>最近一次主线程 Pump 耗时，供项目性能诊断读取。</summary>
    public double LastPumpMilliseconds => Volatile.Read(ref _lastPumpMilliseconds);

    /// <summary>主线程复制网格快照的累计耗时，单位毫秒。</summary>
    public double MeshSnapshotMilliseconds => ToMilliseconds(Interlocked.Read(ref _meshSnapshotElapsedTicks));

    /// <summary>主线程复制网格快照的累计托管分配量。</summary>
    public long MeshSnapshotAllocatedBytes => Interlocked.Read(ref _meshSnapshotAllocatedBytes);

    /// <summary>主线程编译区块网格的累计耗时，单位毫秒。</summary>
    public double MeshBuildMilliseconds => ToMilliseconds(Interlocked.Read(ref _meshBuildElapsedTicks));

    /// <summary>worker 编译区块网格的累计托管分配量。</summary>
    public long MeshBuildAllocatedBytes => Interlocked.Read(ref _meshBuildAllocatedBytes);

    /// <summary>worker 写入视距外区块快照的累计耗时，单位毫秒。</summary>
    public double UnloadSaveMilliseconds => ToMilliseconds(Interlocked.Read(ref _unloadSaveElapsedTicks));

    /// <summary>worker 写入视距外区块快照的累计托管分配量。</summary>
    public long UnloadSaveAllocatedBytes => Interlocked.Read(ref _unloadSaveAllocatedBytes);

    /// <summary>当前仍在等待后台保存完成的视距外区块数量。</summary>
    public int InFlightUnloadSaveCount => _unloadSaveJobs.Count;

    /// <summary>已完成后台持久化的视距外区块数量，包含保存后重新进入视距而未卸载的区块。</summary>
    public int CompletedUnloadSaveCount => Volatile.Read(ref _completedUnloadSaveCount);

    /// <summary>后台保存确认后已释放区块及其关联资源的数量。</summary>
    public int CompletedUnloadCount => Volatile.Read(ref _completedUnloadCount);

    /// <summary>后台区块保存失败次数；失败时 live 区块保留并在后续帧重试。</summary>
    public int UnloadSaveFailureCount => Volatile.Read(ref _unloadSaveFailureCount);

    /// <summary>最近一次后台区块保存失败信息；为空表示当前会话尚未遇到失败。</summary>
    public string? LastUnloadSaveFailure => Volatile.Read(ref _lastUnloadSaveFailure);

    public int PendingCount =>
        _generationQueue.Count + _generationJobs.Count + _generationResults.Count + _completedGenerations.Count +
        _generatedLightingQueue.Count +
        _boundaryRepairQueue.Count + _meshQueue.Count + _meshJobs.Count + _completedMeshes.Count +
        _finalMeshTargets.Count + _pendingEditMeshKeys.Count + _unloadQueue.Count + _unloadSaveJobs.Count;

    /// <summary>读取当前活动范围内已加载和已完成网格化的区块数量；只能在创建流的主线程调用。</summary>
    public ChunkStreamingProgress GetActiveAreaProgress()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureOwnerThread();
        if (!_hasActiveArea)
        {
            return default;
        }

        return GetAreaProgress(_activeCenterX, _activeCenterZ, _activeRadius);
    }

    /// <summary>读取活动范围内指定子窗口的区块进度，适合碰撞或出生点等局部就绪判断。</summary>
    public ChunkStreamingProgress GetAreaProgress(int centerX, int centerZ, int radius)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureOwnerThread();
        if (radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "区块查询半径不能为负数");
        }

        if (!_hasActiveArea ||
            Math.Abs(centerX - _activeCenterX) + radius > _activeRadius ||
            Math.Abs(centerZ - _activeCenterZ) + radius > _activeRadius)
        {
            return default;
        }

        var diameter = radius * 2 + 1;
        var total = diameter * diameter;
        var loaded = 0;
        var meshed = 0;
        for (var z = centerZ - radius; z <= centerZ + radius; z++)
        {
            for (var x = centerX - radius; x <= centerX + radius; x++)
            {
                if (!_world.Store.HasChunk(x, z))
                {
                    continue;
                }

                loaded++;
                if (_world.Store.GetStatus(x, z) >= ChunkStatus.Ready)
                {
                    meshed++;
                }
            }
        }

        return new ChunkStreamingProgress(
            HasActiveArea: true,
            CenterX: centerX,
            CenterZ: centerZ,
            Radius: radius,
            TotalChunks: total,
            LoadedChunks: loaded,
            MeshedChunks: meshed,
            PendingCount: PendingCount);
    }

    /// <summary>在主线程按当前范围的稳定 (z,x) 顺序提交缺失区块。</summary>
    public void RequestArea(int centerCx, int centerCz, int radius) =>
        RequestArea(centerCx, centerCz, new ChunkTicketDistances(radius, radius, radius));

    /// <summary>请求渲染范围，并为已加载区块应用独立的方块 tick/实体模拟 ticket。</summary>
    public void RequestArea(int centerCx, int centerCz, ChunkTicketDistances distances)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureOwnerThread();

        _activeCenterX = centerCx;
        _activeCenterZ = centerCz;
        _activeRadius = distances.RenderRadius;
        _activeDistances = distances;
        _hasActiveArea = true;
        CancelGenerationRequestsOutsideActiveArea();
        CancelMeshRequestsOutsideActiveArea();

        if (_storage is IChunkStorageWriter)
        {
            QueueUnloadRequests();
        }

        _world.UpdateTicketLevels(centerCx, centerCz, distances);

        // 先生成观察点中心，随后按环向外扩展；稳定坐标次序保证同一输入可复现。
        foreach (var (x, z) in _world.FindMissingChunks(centerCx, centerCz, distances.RenderRadius)
                     .OrderBy(key => Math.Max(Math.Abs(key.X - centerCx), Math.Abs(key.Z - centerCz)))
                     .ThenBy(key => (key.X - centerCx) * (key.X - centerCx) + (key.Z - centerCz) * (key.Z - centerCz))
                     .ThenBy(key => key.Z)
                     .ThenBy(key => key.X))
        {
            var key = (x, z);
            if (_generationQueued.Add(key))
            {
                _generationQueue.Enqueue(new GenerationRequest(
                    _nextSequence++,
                    x,
                    z,
                    Stopwatch.GetTimestamp(),
                    _pumpFrame));
            }
        }

        ReorderGenerationQueueForActiveArea();
    }

    /// <summary>主线程协调一次生成、提交、快照、编译和发布流水线。</summary>
    public void Pump()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureOwnerThread();
        var started = Stopwatch.GetTimestamp();
        try
        {
        _pumpFrame++;
        ReleasePendingEditMeshRequests();
        Volatile.Write(ref _lastPumpThreadId, Environment.CurrentManagedThreadId);
        CompleteGenerationJobs();
        CompleteMeshJobs();
        ProcessGeneratedLighting();
        CommitGenerationResults(started);
        DispatchGenerationJobs();
        PublishCompletedMeshes();
        ProcessMeshQueue();
        ProcessPendingUnloads(started);
        }
        finally
        {
            Volatile.Write(
                ref _lastPumpMilliseconds,
                (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency);
        }
    }

    private void DispatchGenerationJobs()
    {
        var limit = Math.Max(1, _jobs.WorkerCount * 2);
        while (_generationJobs.Count < limit && _generationQueue.Count > 0)
        {
            var request = _generationQueue.Dequeue();
            var handle = _jobs.Schedule(token =>
            {
                var started = Stopwatch.GetTimestamp();
                var allocationStarted = GC.GetAllocatedBytesForCurrentThread();
                var generatedSuccessfully = false;
                try
                {
                    if (_diagnostics is not null)
                    {
                        ReportDiagnostic(
                            ChunkStreamingStage.TicketWait,
                            request.X,
                            request.Z,
                            request.Sequence,
                            started - request.EnqueuedAtTicks,
                            0,
                            loadedFromStorage: false,
                            frame: request.RequestedFrame);
                    }

                    PersistedChunk persisted = default;
                    var loadedFromStorage = false;
                    if (_storage is { } storage)
                    {
                        if (_diagnostics is null)
                        {
                            loadedFromStorage = storage.TryLoadChunk(request.X, request.Z, out persisted);
                        }
                        else
                        {
                            var storageReadStarted = Stopwatch.GetTimestamp();
                            var storageReadAllocationStarted = GC.GetAllocatedBytesForCurrentThread();
                            loadedFromStorage = storage.TryLoadChunk(request.X, request.Z, out persisted);
                            ReportDiagnostic(
                                ChunkStreamingStage.StorageRead,
                                request.X,
                                request.Z,
                                request.Sequence,
                                Stopwatch.GetTimestamp() - storageReadStarted,
                                GC.GetAllocatedBytesForCurrentThread() - storageReadAllocationStarted,
                                loadedFromStorage,
                                request.RequestedFrame);
                        }
                    }

                    if (loadedFromStorage)
                    {
                        Interlocked.Increment(ref _loadedCount);
                        _completedGenerations.Enqueue(new GenerationResult(
                            request.Sequence,
                            request.X,
                            request.Z,
                            Chunk: null,
                            Persisted: persisted,
                            FeatureBatches: null));
                        return;
                    }

                    var generated = _world.CreateGeneratedChunkData(
                        request.X,
                        request.Z,
                        token,
                        captureTiming: _diagnostics is not null);
                    if (_diagnostics is not null)
                    {
                        ReportDiagnostic(
                            ChunkStreamingStage.Generation,
                            request.X,
                            request.Z,
                            request.Sequence,
                            generated.Timing.GenerationElapsedTicks,
                            generated.Timing.GenerationAllocatedBytes,
                            loadedFromStorage: false,
                            frame: request.RequestedFrame);
                        ReportDiagnostic(
                            ChunkStreamingStage.InitialLighting,
                            request.X,
                            request.Z,
                            request.Sequence,
                            generated.Timing.InitialLightingElapsedTicks,
                            generated.Timing.InitialLightingAllocatedBytes,
                            loadedFromStorage: false,
                            frame: request.RequestedFrame);
                    }
                    _completedGenerations.Enqueue(new GenerationResult(
                        request.Sequence,
                        request.X,
                        request.Z,
                        generated.Chunk,
                        Persisted: null,
                        generated.FeatureBatches));
                    generatedSuccessfully = true;
                }
                finally
                {
                    Interlocked.Add(ref _generationWorkerElapsedTicks, Stopwatch.GetTimestamp() - started);
                    Interlocked.Add(
                        ref _generationWorkerAllocatedBytes,
                        GC.GetAllocatedBytesForCurrentThread() - allocationStarted);
                    if (generatedSuccessfully)
                    {
                        // 仅在 worker 已完成本次生成的所有收尾工作后对外可见，
                        // 使该计数可作为主线程等待可提交结果的条件。
                        Interlocked.Increment(ref _generatedCount);
                    }
                }
            }, $"voxel-generate-{request.X}-{request.Z}", _cancellation.Token);
            _generationJobs.Add(request.Sequence, new GenerationJob(request, handle));
        }
    }

    private void CompleteGenerationJobs()
    {
        foreach (var (sequence, job) in _generationJobs.ToArray())
        {
            if (!job.Handle.IsCompleted)
            {
                continue;
            }

            _generationJobs.Remove(sequence);
            try
            {
                job.Handle.Complete();
            }
            catch (OperationCanceledException) when (job.Handle.IsCancellationRequested)
            {
            }
            catch
            {
                // 失败序列必须从去重集合移除；同时标记为可跳过，避免后续序列被卡在提交栅栏。
                _generationQueued.Remove((job.Request.X, job.Request.Z));
                _canceledGenerationSequences.Add(sequence);
                throw;
            }
        }

        while (_completedGenerations.TryDequeue(out var result))
        {
            if (_canceledGenerationSequences.Contains(result.Sequence))
            {
                continue;
            }

            _generationResults[result.Sequence] = result;
        }
    }

    private void CommitGenerationResults(long pumpStarted)
    {
        var allocationStarted = GC.GetAllocatedBytesForCurrentThread();
        try
        {
        var commitCount = 0;
        while (commitCount < MaxGenerationCommitsPerPump &&
            (commitCount == 0 || HasRemainingCommitBudget(pumpStarted)))
        {
            if (_canceledGenerationSequences.Remove(_nextCommitSequence))
            {
                _nextCommitSequence++;
                commitCount++;
                continue;
            }

            if (!_generationResults.Remove(_nextCommitSequence, out var result))
            {
                break;
            }

            var commitStarted = Stopwatch.GetTimestamp();
            var commitAllocationStarted = _diagnostics is not null
                ? GC.GetAllocatedBytesForCurrentThread()
                : 0L;
            _generationQueued.Remove((result.X, result.Z));
            if (_hasActiveArea &&
                (Math.Abs(result.X - _activeCenterX) > _activeRadius ||
                 Math.Abs(result.Z - _activeCenterZ) > _activeRadius))
            {
                _nextCommitSequence++;
                commitCount++;
                continue;
            }

            var isGenerated = result.Chunk is not null;
            var committed = result.Persisted is { } persisted
                ? _world.CommitLoadedChunk(result.X, result.Z, persisted)
                : result.Chunk is not null && _world.CommitGeneratedChunkDeferredLighting(
                    result.X,
                    result.Z,
                    result.Chunk,
                    result.FeatureBatches);
            if (committed)
            {
                foreach (var (featureX, featureZ) in _world.ConsumeFeatureLightingDirtyChunkKeys())
                {
                    QueueGeneratedLightingWork(featureX, featureZ);
                }

                foreach (var (featureX, featureZ) in _world.ConsumeFeatureMeshDirtyChunkKeys())
                {
                    QueueBoundaryRepairRequest(featureX, featureZ);
                }

                _world.UpdateTicketLevels(_activeCenterX, _activeCenterZ, _activeDistances);
                if (isGenerated)
                {
                    QueueGeneratedLightingWork(result.X, result.Z);
                }
                else
                {
                    QueueGeneratedMeshRequest(result.X, result.Z);
                    foreach (var (dx, dz) in HorizontalNeighbours)
                    {
                        if (_world.Store.HasChunk(result.X + dx, result.Z + dz))
                        {
                            QueueBoundaryRepairRequest(result.X + dx, result.Z + dz);
                        }
                    }
                }
            }

            if (_diagnostics is not null)
            {
                ReportDiagnostic(
                    ChunkStreamingStage.GenerationCommit,
                    result.X,
                    result.Z,
                    result.Sequence,
                    Stopwatch.GetTimestamp() - commitStarted,
                    GC.GetAllocatedBytesForCurrentThread() - commitAllocationStarted,
                    result.Persisted is not null,
                    _pumpFrame);
            }
            _nextCommitSequence++;
            commitCount++;
            Interlocked.Add(ref _generationCommitElapsedTicks, Stopwatch.GetTimestamp() - commitStarted);
        }

        Volatile.Write(ref _lastGenerationCommitCount, commitCount);
        }
        finally
        {
            Interlocked.Add(
                ref _generationCommitAllocatedBytes,
                GC.GetAllocatedBytesForCurrentThread() - allocationStarted);
        }
    }

    private void ProcessMeshQueue()
    {
        // 已提交区块可在后续生成仍在进行时分批网格化；邻区到达会重新请求边界网格。
        if (!HasPendingGenerationWork() && _finalMeshTargets.Count > 0)
        {
            _boundaryRepairQueue.Clear();
            _meshQueue.Clear();
            _meshQueued.Clear();
            foreach (var key in _finalMeshTargets.OrderBy(key => key.Z).ThenBy(key => key.X))
            {
                QueueMeshRequest(key.X, key.Z);
            }

            _finalMeshTargets.Clear();
        }

        var snapshots = 0;
        while (snapshots < MaxMeshSnapshotsPerPump && (_boundaryRepairQueue.Count > 0 || _meshQueue.Count > 0))
        {
            var queue = _boundaryRepairQueue.Count > 0 ? _boundaryRepairQueue : _meshQueue;
            var node = queue.First!;
            var key = node.Value;

            // 同一区块已有编译任务时保留该脏请求到下一帧。
            // 这样新 revision 不会丢弃仍可发布的边界修复结果。
            if (_meshInFlight.Contains(key))
            {
                return;
            }

            queue.RemoveFirst();
            _meshQueued.Remove(key);
            var snapshotStarted = Stopwatch.GetTimestamp();
            var snapshotAllocationStarted = GC.GetAllocatedBytesForCurrentThread();
            var work = _world.CreatePooledMeshSnapshot(key.X, key.Z);
            var snapshotElapsedTicks = Stopwatch.GetTimestamp() - snapshotStarted;
            var snapshotAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - snapshotAllocationStarted;
            Interlocked.Add(ref _meshSnapshotElapsedTicks, snapshotElapsedTicks);
            Interlocked.Add(
                ref _meshSnapshotAllocatedBytes,
                snapshotAllocatedBytes);
            if (work is null)
            {
                continue;
            }

            var snapshot = work.Value.Snapshot;
            var revision = work.Value.Revision;
            var sequence = _nextMeshSequence++;
            var requestedFrame = _pumpFrame;
            if (_diagnostics is not null)
            {
                ReportDiagnostic(
                    ChunkStreamingStage.MeshSnapshot,
                    key.X,
                    key.Z,
                    sequence,
                    snapshotElapsedTicks,
                    snapshotAllocatedBytes,
                    loadedFromStorage: false,
                    frame: requestedFrame);
            }
            JobHandle handle;
            try
            {
                handle = _jobs.Schedule(token =>
                {
                    var meshStarted = Stopwatch.GetTimestamp();
                    var allocationStarted = GC.GetAllocatedBytesForCurrentThread();
                    var geometry = default(ChunkMeshGeometry);
                    try
                    {
                        Volatile.Write(ref _lastMeshThreadId, Environment.CurrentManagedThreadId);
                        var mesh = ChunkMesher.Mesh(snapshot, token);
                        geometry = mesh.Geometry;
                        _completedMeshes.Enqueue(new MeshResult(
                            sequence,
                            key.X,
                            key.Z,
                            revision,
                            mesh,
                            requestedFrame));
                    }
                    finally
                    {
                        snapshot.Dispose();
                        var meshElapsedTicks = Stopwatch.GetTimestamp() - meshStarted;
                        var meshAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStarted;
                        Interlocked.Add(ref _meshBuildElapsedTicks, meshElapsedTicks);
                        Interlocked.Add(
                            ref _meshBuildAllocatedBytes,
                            meshAllocatedBytes);
                        if (_diagnostics is not null)
                        {
                            ReportDiagnostic(
                                ChunkStreamingStage.MeshBuild,
                                key.X,
                                key.Z,
                                sequence,
                                meshElapsedTicks,
                                meshAllocatedBytes,
                                loadedFromStorage: false,
                                frame: requestedFrame,
                                geometry: geometry);
                        }
                    }
                }, $"voxel-mesh-{key.X}-{key.Z}", _cancellation.Token);
            }
            catch
            {
                snapshot.Dispose();
                throw;
            }

            _meshInFlight.Add(key);
            _meshJobs.Add(sequence, new MeshJob(key, handle));
            snapshots++;
        }
    }

    private void ProcessGeneratedLighting()
    {
        if (_generatedLightingQueue.Count == 0)
        {
            return;
        }

        var work = _generatedLightingQueue.Dequeue();
        if (_diagnostics is null)
        {
            var completedWithoutDiagnostics = _world.SynchronizeGeneratedChunkLightingStep(work.X, work.Z, work.Step);
            ContinueGeneratedLightingWork(work, completedWithoutDiagnostics);
            return;
        }

        var lightingStarted = Stopwatch.GetTimestamp();
        var lightingAllocationStarted = GC.GetAllocatedBytesForCurrentThread();
        var completed = _world.SynchronizeGeneratedChunkLightingStep(work.X, work.Z, work.Step);
        ReportDiagnostic(
            ChunkStreamingStage.BoundaryLighting,
            work.X,
            work.Z,
            0,
            Stopwatch.GetTimestamp() - lightingStarted,
            GC.GetAllocatedBytesForCurrentThread() - lightingAllocationStarted,
            loadedFromStorage: false,
            frame: _pumpFrame);
        ContinueGeneratedLightingWork(work, completed);
    }

    private void ContinueGeneratedLightingWork(GeneratedLightingWork work, bool completed)
    {
        if (!completed)
        {
            _generatedLightingQueue.Enqueue(work with { Step = work.Step + 1 });
            return;
        }

        var key = (work.X, work.Z);
        if (_generatedLightingRequeue.Remove(key))
        {
            _generatedLightingQueue.Enqueue(new GeneratedLightingWork(work.X, work.Z, 0));
            return;
        }

        _generatedLightingQueued.Remove(key);

        if (!_world.Store.HasChunk(work.X, work.Z))
        {
            return;
        }

        QueueGeneratedMeshRequest(work.X, work.Z);
        foreach (var (dx, dz) in HorizontalNeighbours)
        {
            if (_world.Store.HasChunk(work.X + dx, work.Z + dz))
            {
                QueueBoundaryRepairRequest(work.X + dx, work.Z + dz);
            }
        }
    }

    private void CompleteMeshJobs()
    {
        foreach (var (sequence, job) in _meshJobs.ToArray())
        {
            if (!job.Handle.IsCompleted)
            {
                continue;
            }

            _meshJobs.Remove(sequence);
            _meshInFlight.Remove(job.Key);
            try
            {
                job.Handle.Complete();
            }
            catch (OperationCanceledException) when (job.Handle.IsCancellationRequested)
            {
            }
        }
    }

    private void PublishCompletedMeshes()
    {
        var processed = 0;
        while (processed < MaxMeshPublishesPerPump && _completedMeshes.TryDequeue(out var result))
        {
            processed++;
            if (_canceledMeshSequences.Remove(result.Sequence))
            {
                continue;
            }

            var publishStarted = _diagnostics is not null ? Stopwatch.GetTimestamp() : 0L;
            var publishAllocationStarted = _diagnostics is not null
                ? GC.GetAllocatedBytesForCurrentThread()
                : 0L;
            var published = _world.PublishMesh(result.X, result.Z, result.Revision, result.Mesh);
            if (published)
            {
                if (_diagnostics is not null)
                {
                    ReportDiagnostic(
                        ChunkStreamingStage.MeshPublish,
                        result.X,
                        result.Z,
                        result.Sequence,
                        Stopwatch.GetTimestamp() - publishStarted,
                        GC.GetAllocatedBytesForCurrentThread() - publishAllocationStarted,
                        loadedFromStorage: false,
                        frame: result.RequestedFrame,
                        geometry: result.Mesh.Geometry);
                }

                Interlocked.Increment(ref _meshedCount);
            }
        }

        foreach (var sequence in _canceledMeshSequences.ToArray())
        {
            if (!_meshJobs.ContainsKey(sequence))
            {
                _canceledMeshSequences.Remove(sequence);
            }
        }
    }

    /// <summary>请求网格；同一区块在主线程队列中只保留一个请求。</summary>
    public void QueueMeshRequest(int cx, int cz)
    {
        QueueMeshRequest(cx, cz, prioritize: false);
    }

    /// <summary>登记项目挖放网格脏区；连续编辑结束后才提交最新快照。</summary>
    public void QueueMeshEditRequest(int cx, int cz)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureOwnerThread();
        var key = (cx, cz);
        if (HasPendingGenerationWork())
        {
            _finalMeshTargets.Add(key);
        }

        if (_meshQueued.Remove(key, out var existing))
        {
            var queue = existing.IsBoundaryRepair ? _boundaryRepairQueue : _meshQueue;
            queue.Remove(existing.Node);
        }

        _pendingEditMeshKeys.Add(key);
        _lastEditFrames[key] = _pumpFrame;
    }

    private void QueueMeshRequest(int cx, int cz, bool prioritize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureOwnerThread();
        (int X, int Z) key = (cx, cz);
        if (prioritize)
        {
            _pendingEditMeshKeys.Remove(key);
            _lastEditFrames.Remove(key);
        }
        if (HasPendingGenerationWork())
        {
            _finalMeshTargets.Add(key);
        }

        if (_meshQueued.Remove(key, out var existing))
        {
            var queue = existing.IsBoundaryRepair ? _boundaryRepairQueue : _meshQueue;
            queue.Remove(existing.Node);
        }

        var targetQueue = prioritize ? _boundaryRepairQueue : _meshQueue;
        var node = targetQueue.AddLast(key);
        _meshQueued.Add(key, new MeshQueueEntry(prioritize, node));
    }

    private void QueueGeneratedMeshRequest(int cx, int cz)
    {
        _finalMeshTargets.Add((cx, cz));
        QueueMeshRequest(cx, cz, prioritize: false);
    }

    /// <summary>同一区块的多次特征写入合并为一次完整光照；处理中再次变更则完成后重跑一次。</summary>
    private void QueueGeneratedLightingWork(int cx, int cz)
    {
        var key = (cx, cz);
        if (_generatedLightingQueued.Add(key))
        {
            _generatedLightingQueue.Enqueue(new GeneratedLightingWork(cx, cz, 0));
        }
        else
        {
            _generatedLightingRequeue.Add(key);
        }
    }

    /// <summary>邻区到达后优先修复已有区块的边界面，避免持续流式时出现陈旧接缝。</summary>
    private void QueueBoundaryRepairRequest(int cx, int cz)
    {
        _finalMeshTargets.Add((cx, cz));
        QueueMeshRequest(cx, cz, prioritize: true);
    }

    private void ReleasePendingEditMeshRequests()
    {
        foreach (var key in _pendingEditMeshKeys.ToArray())
        {
            if (_pumpFrame - _lastEditFrames.GetValueOrDefault(key) < EditMeshQuietFrames)
            {
                continue;
            }

            _pendingEditMeshKeys.Remove(key);
            _lastEditFrames.Remove(key);
            QueueMeshRequest(key.X, key.Z, prioritize: false);
        }
    }

    private bool HasPendingGenerationWork() =>
        _generationQueue.Count > 0 || _generationJobs.Count > 0 || _generationResults.Count > 0 ||
        !_completedGenerations.IsEmpty || _generationQueued.Count > 0 || _generatedLightingQueue.Count > 0;

    /// <summary>把离开渲染范围的区块排队；不能在观察点切换的调用栈中同步写盘或释放资源。</summary>
    private void QueueUnloadRequests()
    {
        foreach (var key in _world.Store.ChunkKeys
                     .Where(key => !IsInsideActiveArea(key.X, key.Z))
                     .OrderByDescending(key => Math.Max(
                         Math.Abs(key.X - _activeCenterX),
                         Math.Abs(key.Z - _activeCenterZ)))
                     .ThenBy(key => key.Z)
                     .ThenBy(key => key.X))
        {
            if (!_unloadSaveJobs.ContainsKey(key) && _unloadQueued.Add(key))
            {
                _unloadQueue.Enqueue(key);
            }
        }
    }

    /// <summary>
    /// 回收已完成的保存并在预算允许时投递一个新的快照保存。
    /// 区块对象、Store、网格与 GPU 资源始终只在主线程保存完成后释放。
    /// </summary>
    private void ProcessPendingUnloads(long pumpStarted)
    {
        CompletePendingUnloadSaves();

        if (_storage is not IChunkStorageWriter storage ||
            _unloadQueue.Count == 0 ||
            _unloadSaveJobs.Count >= MaxInFlightUnloadSaves ||
            !HasRemainingCommitBudget(pumpStarted))
        {
            return;
        }

        var key = _unloadQueue.Dequeue();
        _unloadQueued.Remove(key);
        if (IsInsideActiveArea(key.X, key.Z) || !_world.Store.HasChunk(key.X, key.Z))
        {
            return;
        }

        if (!_world.TryCreatePersistenceSnapshot(key.X, key.Z, out var snapshot, out var requiresSave))
        {
            return;
        }

        if (!requiresSave)
        {
            if (_world.UnloadChunk(key.X, key.Z))
            {
                Interlocked.Increment(ref _completedUnloadCount);
            }

            return;
        }

        var handle = _jobs.Schedule(token =>
        {
            var started = Stopwatch.GetTimestamp();
            var allocationStarted = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                token.ThrowIfCancellationRequested();
                storage.SaveChunk(key.X, key.Z, snapshot);
            }
            finally
            {
                Interlocked.Add(ref _unloadSaveElapsedTicks, Stopwatch.GetTimestamp() - started);
                Interlocked.Add(
                    ref _unloadSaveAllocatedBytes,
                    GC.GetAllocatedBytesForCurrentThread() - allocationStarted);
            }
        }, $"voxel-save-unload-{key.X}-{key.Z}", _cancellation.Token);
        _unloadSaveJobs.Add(key, new UnloadSaveJob(key, snapshot, handle));
    }

    /// <summary>
    /// 后台保存完成后，由主线程确认快照仍对应 live 区块才清脏并释放资源。
    /// 保存失败或保存期间发生编辑时，保留区块并重新进入卸载队列。
    /// </summary>
    private void CompletePendingUnloadSaves()
    {
        foreach (var (key, job) in _unloadSaveJobs.ToArray())
        {
            if (!job.Handle.IsCompleted)
            {
                continue;
            }

            _unloadSaveJobs.Remove(key);
            try
            {
                job.Handle.Complete();
            }
            catch (OperationCanceledException) when (job.Handle.IsCancellationRequested)
            {
                continue;
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _unloadSaveFailureCount);
                Volatile.Write(ref _lastUnloadSaveFailure, exception.Message);
                RequeueUnloadIfStillOutside(key);
                continue;
            }

            Interlocked.Increment(ref _completedUnloadSaveCount);
            if (IsInsideActiveArea(key.X, key.Z))
            {
                continue;
            }

            if (_world.TryUnloadPersistedSnapshot(key.X, key.Z, job.Snapshot))
            {
                Interlocked.Increment(ref _completedUnloadCount);
            }
            else
            {
                RequeueUnloadIfStillOutside(key);
            }
        }
    }

    private void RequeueUnloadIfStillOutside((int X, int Z) key)
    {
        if (!IsInsideActiveArea(key.X, key.Z) &&
            _world.Store.HasChunk(key.X, key.Z) &&
            _unloadQueued.Add(key))
        {
            _unloadQueue.Enqueue(key);
        }
    }

    private static bool HasRemainingCommitBudget(long pumpStarted) =>
        (Stopwatch.GetTimestamp() - pumpStarted) * 1000d / Stopwatch.Frequency <
        MainThreadCommitBudgetMilliseconds;

    private void ReorderGenerationQueueForActiveArea()
    {
        if (!_hasActiveArea || _generationQueue.Count < 2)
        {
            return;
        }

        var ordered = _generationQueue
            .OrderBy(request => Math.Max(
                Math.Abs(request.X - _activeCenterX),
                Math.Abs(request.Z - _activeCenterZ)))
            .ThenBy(request =>
                (request.X - _activeCenterX) * (request.X - _activeCenterX) +
                (request.Z - _activeCenterZ) * (request.Z - _activeCenterZ))
            .ThenBy(request => request.Z)
            .ThenBy(request => request.X)
            .ThenBy(request => request.Sequence)
            .ToArray();
        _generationQueue.Clear();
        foreach (var request in ordered)
        {
            _generationQueue.Enqueue(request);
        }
    }

    private static readonly (int X, int Z)[] HorizontalNeighbours =
    [
        (1, 0), (-1, 0), (0, 1), (0, -1),
    ];

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        EnsureOwnerThread();
        _disposed = true;
        Exception? failure = null;
        try
        {
            _cancellation.Cancel();
            failure = CompleteAndClearJobs();
        }
        finally
        {
            _generationQueue.Clear();
            _generationQueued.Clear();
            _generationResults.Clear();
            _canceledGenerationSequences.Clear();
            _generatedLightingQueue.Clear();
            _generatedLightingQueued.Clear();
            _generatedLightingRequeue.Clear();
            _unloadQueue.Clear();
            _unloadQueued.Clear();
            _unloadSaveJobs.Clear();
            _meshJobs.Clear();
            _meshInFlight.Clear();
            _canceledMeshSequences.Clear();
            while (_completedMeshes.TryDequeue(out _))
            {
            }
            _boundaryRepairQueue.Clear();
            _meshQueue.Clear();
            _meshQueued.Clear();
            _finalMeshTargets.Clear();
            _cancellation.Dispose();
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private Exception? CompleteAndClearJobs()
    {
        Exception? failure = null;
        foreach (var job in _generationJobs.Values)
        {
            try
            {
                job.Handle.Complete();
            }
            catch (OperationCanceledException) when (job.Handle.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        }

        _generationJobs.Clear();
        while (_completedGenerations.TryDequeue(out _))
        {
        }

        foreach (var job in _meshJobs.Values)
        {
            try
            {
                job.Handle.Complete();
            }
            catch (OperationCanceledException) when (job.Handle.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        }

        _meshJobs.Clear();
        _meshInFlight.Clear();
        while (_completedMeshes.TryDequeue(out _))
        {
        }

        foreach (var job in _unloadSaveJobs.Values)
        {
            try
            {
                job.Handle.Complete();
            }
            catch (OperationCanceledException) when (job.Handle.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        }

        _unloadSaveJobs.Clear();

        return failure;
    }

    private void CancelMeshRequestsOutsideActiveArea()
    {
        if (!_hasActiveArea)
        {
            return;
        }

        foreach (var (key, entry) in _meshQueued.ToArray())
        {
            if (IsInsideActiveArea(key.X, key.Z))
            {
                continue;
            }

            var queue = entry.IsBoundaryRepair ? _boundaryRepairQueue : _meshQueue;
            queue.Remove(entry.Node);
            _meshQueued.Remove(key);
            _finalMeshTargets.Remove(key);
        }

        foreach (var (sequence, job) in _meshJobs.ToArray())
        {
            if (IsInsideActiveArea(job.Key.X, job.Key.Z))
            {
                continue;
            }

            _finalMeshTargets.Remove(job.Key);
            if (_canceledMeshSequences.Add(sequence))
            {
                job.Handle.Cancel();
                Interlocked.Increment(ref _canceledMeshCount);
            }
        }

        foreach (var key in _pendingEditMeshKeys.Where(key => !IsInsideActiveArea(key.X, key.Z)).ToArray())
        {
            _pendingEditMeshKeys.Remove(key);
            _lastEditFrames.Remove(key);
        }

        // 结果可能在本次视距切换前已完成但尚未发布；丢弃范围外结果，
        // 并用序列集合覆盖 worker 在清空之后才入队的竞态。
        var retained = new List<MeshResult>();
        while (_completedMeshes.TryDequeue(out var result))
        {
            if (IsInsideActiveArea(result.X, result.Z))
            {
                retained.Add(result);
            }
            else
            {
                _canceledMeshSequences.Add(result.Sequence);
            }
        }

        foreach (var result in retained)
        {
            _completedMeshes.Enqueue(result);
        }
    }

    private void CancelGenerationRequestsOutsideActiveArea()
    {
        if (!_hasActiveArea)
        {
            return;
        }

        var retained = new Queue<GenerationRequest>();
        while (_generationQueue.Count > 0)
        {
            var request = _generationQueue.Dequeue();
            if (IsInsideActiveArea(request.X, request.Z))
            {
                retained.Enqueue(request);
                continue;
            }

            _generationQueued.Remove((request.X, request.Z));
            _canceledGenerationSequences.Add(request.Sequence);
            Interlocked.Increment(ref _canceledGenerationCount);
        }

        while (retained.Count > 0)
        {
            _generationQueue.Enqueue(retained.Dequeue());
        }

        foreach (var (sequence, job) in _generationJobs.ToArray())
        {
            if (IsInsideActiveArea(job.Request.X, job.Request.Z))
            {
                continue;
            }

            _generationQueued.Remove((job.Request.X, job.Request.Z));
            if (_canceledGenerationSequences.Add(sequence))
            {
                job.Handle.Cancel();
                Interlocked.Increment(ref _canceledGenerationCount);
            }
        }

        foreach (var (sequence, result) in _generationResults.ToArray())
        {
            if (IsInsideActiveArea(result.X, result.Z))
            {
                continue;
            }

            _generationResults.Remove(sequence);
            _generationQueued.Remove((result.X, result.Z));
            if (_canceledGenerationSequences.Add(sequence))
            {
                Interlocked.Increment(ref _canceledGenerationCount);
            }
        }
    }

    private bool IsInsideActiveArea(int cx, int cz) =>
        Math.Abs(cx - _activeCenterX) <= _activeRadius &&
        Math.Abs(cz - _activeCenterZ) <= _activeRadius;

    private void ReportDiagnostic(
        ChunkStreamingStage stage,
        int chunkX,
        int chunkZ,
        long sequence,
        long elapsedTicks,
        long allocatedBytes,
        bool loadedFromStorage,
        long frame,
        ChunkMeshGeometry geometry = default)
    {
        var diagnostics = _diagnostics;
        if (diagnostics is null)
        {
            return;
        }

        diagnostics.Report(new ChunkStreamingDiagnosticSample(
            stage,
            chunkX,
            chunkZ,
            sequence,
            elapsedTicks,
            allocatedBytes,
            loadedFromStorage,
            frame,
            geometry));
    }

    private static double ToMilliseconds(long stopwatchTicks) => stopwatchTicks * 1000d / Stopwatch.Frequency;

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("ChunkStreamer 只能在创建它的主线程调用");
        }
    }

}
