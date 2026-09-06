namespace Cubit.Voxel.World;

/// <summary>
/// 区块集合：以世界坐标查询方块，自动定位所属区块。
/// 线程安全（SyncRoot 保护）；维护每区块的生成状态（ChunkStatus）与加载等级（TicketLevel）。
/// </summary>
public sealed class ChunkStore
{
    private readonly Dictionary<(int X, int Z), Chunk> _chunks = [];
    private readonly Dictionary<(int X, int Z), ChunkStatus> _status = [];
    private readonly Dictionary<(int X, int Z), TicketLevel> _tickets = [];

    /// <summary>同步锁：多线程（流式生成/主线程读写）共用。</summary>
    public object SyncRoot { get; } = new();

    public static (int X, int Z) WorldToChunk(int x, int z)
    {
        var cx = (int)MathF.Floor((float)x / Chunk.SizeX);
        var cz = (int)MathF.Floor((float)z / Chunk.SizeZ);
        return (cx, cz);
    }

    public static (int X, int Y, int Z) WorldToLocal(int x, int y, int z)
    {
        var (cx, cz) = WorldToChunk(x, z);
        var lx = x - cx * Chunk.SizeX;
        var lz = z - cz * Chunk.SizeZ;
        return (lx, y, lz);
    }

    public bool HasChunk(int cx, int cz)
    {
        lock (SyncRoot)
        {
            return _chunks.ContainsKey((cx, cz));
        }
    }

    public Chunk? GetChunk(int cx, int cz)
    {
        lock (SyncRoot)
        {
            return _chunks.GetValueOrDefault((cx, cz));
        }
    }

    /// <summary>在已持有 SyncRoot 时直接读取区块，供体素快照避免重复进入公共锁路径。</summary>
    internal Chunk? GetChunkUnsafe(int cx, int cz) => _chunks.GetValueOrDefault((cx, cz));

    public void AddChunk(int cx, int cz, Chunk chunk)
    {
        lock (SyncRoot)
        {
            _chunks[(cx, cz)] = chunk;
            _status.TryAdd((cx, cz), ChunkStatus.Empty);
            _tickets.TryAdd((cx, cz), TicketLevel.BlockTicks);
        }
    }

    public bool RemoveChunk(int cx, int cz, out Chunk? chunk)
    {
        lock (SyncRoot)
        {
            if (!_chunks.Remove((cx, cz), out chunk))
            {
                return false;
            }

            _status.Remove((cx, cz));
            _tickets.Remove((cx, cz));
            return true;
        }
    }

    public ChunkStatus GetStatus(int cx, int cz)
    {
        lock (SyncRoot)
        {
            return _status.GetValueOrDefault((cx, cz), ChunkStatus.Empty);
        }
    }

    public void SetStatus(int cx, int cz, ChunkStatus status)
    {
        lock (SyncRoot)
        {
            _status[(cx, cz)] = status;
        }
    }

    public TicketLevel GetTicketLevel(int cx, int cz)
    {
        lock (SyncRoot)
        {
            return _tickets.GetValueOrDefault((cx, cz), TicketLevel.DataOnly);
        }
    }

    public void SetTicketLevel(int cx, int cz, TicketLevel level)
    {
        lock (SyncRoot)
        {
            _tickets[(cx, cz)] = level;
        }
    }

    /// <summary>返回区块坐标快照。</summary>
    public (int X, int Z)[] ChunkKeys
    {
        get
        {
            lock (SyncRoot)
            {
                return [.. _chunks.Keys];
            }
        }
    }

    public BlockState GetBlock(int x, int y, int z)
    {
        if (!Chunk.IsValidY(y))
        {
            return BlockState.Air;
        }

        var (cx, cz) = WorldToChunk(x, z);
        lock (SyncRoot)
        {
            var chunk = _chunks.GetValueOrDefault((cx, cz));
            if (chunk is null)
            {
                return BlockState.Air;
            }

            var (lx, _, lz) = WorldToLocal(x, y, z);
            return chunk.GetBlock(lx, y, lz);
        }
    }

    public void SetBlock(int x, int y, int z, BlockState block)
    {
        if (!Chunk.IsValidY(y))
        {
            return;
        }

        var (cx, cz) = WorldToChunk(x, z);
        lock (SyncRoot)
        {
            var chunk = _chunks.GetValueOrDefault((cx, cz));
            if (chunk is null)
            {
                return;
            }

            var (lx, _, lz) = WorldToLocal(x, y, z);
            chunk.SetBlock(lx, y, lz, block);
        }
    }
}
