namespace Cubit.Voxel.World;

/// <summary>单区块持久化快照；读取不会触碰 VoxelWorld 或 ChunkStore。</summary>
public readonly record struct PersistedChunk(Chunk Chunk, ChunkStatus Status, TicketLevel TicketLevel);

/// <summary>体素插件的按坐标区块读取契约；调用方负责把快照提交到世界。</summary>
public interface IChunkStorage
{
    bool TryLoadChunk(int cx, int cz, out PersistedChunk chunk);
}

/// <summary>
/// 可选的体素区块写入契约；读取方仍可只依赖 IChunkStorage。
/// 流式器会在主线程复制独立的 PersistedChunk 后由 worker 调用 SaveChunk，
/// 实现必须自行同步其文件句柄与内部缓存，且不得保留或修改传入区块。
/// </summary>
public interface IChunkStorageWriter : IChunkStorage
{
    void SaveChunk(int cx, int cz, PersistedChunk chunk);
}
