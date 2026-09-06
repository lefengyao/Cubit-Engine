namespace Cubit.Voxel.World;

/// <summary>流式区块在 worker 与主线程边界的通用诊断阶段。</summary>
public enum ChunkStreamingStage
{
    TicketWait,
    StorageRead,
    Generation,
    InitialLighting,
    GenerationCommit,
    BoundaryLighting,
    MeshSnapshot,
    MeshBuild,
    MeshPublish,
    MeshUpload,
}

/// <summary>一条不可变的流式阶段观测；耗时使用 Stopwatch tick，避免依赖墙钟精度。</summary>
public readonly record struct ChunkStreamingDiagnosticSample(
    ChunkStreamingStage Stage,
    int ChunkX,
    int ChunkZ,
    long Sequence,
    long ElapsedTicks,
    long AllocatedBytes,
    bool LoadedFromStorage,
    long Frame,
    ChunkMeshGeometry Geometry = default);

/// <summary>可选诊断接收器；实现方必须支持 worker 线程并发回调。</summary>
public interface IChunkStreamingDiagnosticsSink
{
    void Report(in ChunkStreamingDiagnosticSample sample);
}
