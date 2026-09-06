using System.Text;

namespace Cubit.Voxel.World;

/// <summary>
/// Cubit region 的按区块读取器。
/// region 头和目录项按 region 缓存，区块负载只在请求坐标时读取。
/// </summary>
public sealed class WorldFileStorage : IChunkStorageWriter, IDisposable
{
    private const int DefaultMaxCachedChunks = 512;
    private const int MinimumCompactionStaleBytes = 8 * 1024;
    private const string TemporarySuffix = ".tmp";
    private sealed record RegionIndex(string Path, int Version, int[] Offsets, int[] Lengths);

    private readonly string _directory;
    private readonly int _maxCachedChunks;
    private readonly Dictionary<(int X, int Z), RegionIndex?> _regions = [];
    private readonly Dictionary<
        (int X, int Z),
        (int Version, byte[] Encoded, LinkedListNode<(int X, int Z)> Node)> _chunkCache = [];
    private readonly LinkedList<(int X, int Z)> _chunkCacheOrder = [];
    private readonly object _sync = new();
    private bool _disposed;

    public WorldFileStorage(string directory)
        : this(directory, DefaultMaxCachedChunks)
    {
    }

    /// <summary>打开按区块存储，并限制会话内编码负载缓存条目数。</summary>
    public WorldFileStorage(string directory, int maxCachedChunks)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("世界存档目录不能为空", nameof(directory));
        }

        if (maxCachedChunks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCachedChunks), "区块缓存上限不能为负数");
        }

        _directory = Path.GetFullPath(directory);
        _maxCachedChunks = maxCachedChunks;
        if (!Directory.Exists(_directory))
        {
            throw new DirectoryNotFoundException($"找不到世界存档目录: {_directory}");
        }

        RecoverTemporaryRegions();
    }

    public string DirectoryPath => _directory;

    /// <summary>当前会话缓存的编码区块负载数量，仅用于存储预算诊断。</summary>
    public int CachedChunkCount
    {
        get
        {
            lock (_sync)
            {
                return _chunkCache.Count;
            }
        }
    }

    public int MaxCachedChunks => _maxCachedChunks;

    public bool TryLoadChunk(int cx, int cz, out PersistedChunk chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        chunk = default;
        var key = (cx, cz);
        if (TryGetCachedChunk(key, out var cachedVersion, out var cachedEncoded))
        {
            chunk = DecodeChunkPayload(cachedEncoded, cachedVersion, cx, cz, "缓存", -1);
            return true;
        }

        var (regionX, regionZ) = WorldFile.ChunkToRegion(cx, cz);
        var index = GetRegionIndex(regionX, regionZ);
        if (index is null)
        {
            return false;
        }

        var slot = WorldFile.SlotIndexForStorage(regionX, regionZ, cx, cz);
        var offset = index.Offsets[slot];
        var length = index.Lengths[slot];
        if (length <= 0)
        {
            return false;
        }

        using var stream = File.OpenRead(index.Path);
        if (offset < WorldFile.RegionHeaderBytes || offset > stream.Length - length)
        {
            throw new InvalidDataException($"region 区块目录项越界: {index.Path} slot={slot}");
        }

        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var encoded = reader.ReadBytes(length);
        if (encoded.Length != length)
        {
            throw new InvalidDataException($"region 区块负载截断: {index.Path} slot={slot}");
        }

        chunk = DecodeChunkPayload(encoded, index.Version, cx, cz, index.Path, slot);
        CacheChunk(key, index.Version, encoded);
        return true;
    }

    /// <summary>
    /// 在同目录临时文件中追加负载并更新目录，flush 后再替换正式 region。
    /// 旧正式文件在替换完成前保持可读，进程崩溃后由下一次打开恢复或丢弃临时文件。
    /// </summary>
    public void SaveChunk(int cx, int cz, PersistedChunk persisted)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(persisted.Chunk);
        var (regionX, regionZ) = WorldFile.ChunkToRegion(cx, cz);
        var key = (regionX, regionZ);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var path = WorldFile.RegionPath(_directory, regionX, regionZ);
            var index = GetRegionIndexLocked(regionX, regionZ);
            if (index is not null && index.Version < WorldFile.RegionVersion)
            {
                index = UpgradeRegionLocked(index, regionX, regionZ);
            }

            var regionVersion = WorldFile.RegionVersion;
            var payload = WorldFile.EncodeChunkPayload(
                cx,
                cz,
                persisted.Status,
                persisted.TicketLevel,
                persisted.Chunk,
                regionVersion);
            var offsets = index is null ? new int[WorldFile.SlotsPerRegion] : (int[])index.Offsets.Clone();
            var lengths = index is null ? new int[WorldFile.SlotsPerRegion] : (int[])index.Lengths.Clone();
            var temporaryPath = path + TemporarySuffix;
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            if (File.Exists(path))
            {
                File.Copy(path, temporaryPath, overwrite: false);
            }

            if (!File.Exists(temporaryPath))
            {
                using var headerStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                WriteEmptyRegionHeader(headerStream);
            }

            using (var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                if (stream.Length < WorldFile.RegionHeaderBytes)
                {
                    throw new InvalidDataException($"region 文件头不完整: {path}");
                }

                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
                stream.Seek(0, SeekOrigin.End);
                var offset = checked((int)stream.Position);
                writer.Write(payload);
                writer.Flush();
                stream.Flush(flushToDisk: true);

                var slot = WorldFile.SlotIndexForStorage(regionX, regionZ, cx, cz);
                stream.Seek(WorldFile.RegionDirectoryOffset + slot * sizeof(int) * 2, SeekOrigin.Begin);
                writer.Write(offset);
                writer.Write(payload.Length);
                writer.Flush();
                stream.Flush(flushToDisk: true);

                offsets[slot] = offset;
                lengths[slot] = payload.Length;
            }

            File.Move(temporaryPath, path, overwrite: true);
            var updatedIndex = new RegionIndex(path, regionVersion, offsets, lengths);
            _regions[key] = ShouldCompact(updatedIndex)
                ? CompactRegionLocked(updatedIndex)
                : updatedIndex;
            RemoveCachedChunkLocked((cx, cz));
        }
    }

    /// <summary>
    /// 按 region 批量保存区块。一个 region 只复制一次临时文件并统一刷盘，
    /// 避免退出时逐区块复制/刷盘同一 region 造成 O(n²) 写放大。
    /// </summary>
    public void SaveChunks(IReadOnlyList<(int X, int Z, PersistedChunk Persisted)> chunks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
        {
            return;
        }

        var byRegion = new Dictionary<(int X, int Z), List<(int X, int Z, PersistedChunk Persisted)>>();
        foreach (var entry in chunks)
        {
            ArgumentNullException.ThrowIfNull(entry.Persisted.Chunk);
            var region = WorldFile.ChunkToRegion(entry.X, entry.Z);
            if (!byRegion.TryGetValue(region, out var regionChunks))
            {
                regionChunks = [];
                byRegion.Add(region, regionChunks);
            }

            regionChunks.Add(entry);
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var (region, regionChunks) in byRegion.OrderBy(pair => pair.Key.X).ThenBy(pair => pair.Key.Z))
            {
                SaveRegionChunksLocked(region.X, region.Z, regionChunks);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_sync)
        {
            _regions.Clear();
            _chunkCache.Clear();
            _chunkCacheOrder.Clear();
        }
    }

    private bool TryGetCachedChunk(
        (int X, int Z) key,
        out int version,
        out byte[] encoded)
    {
        lock (_sync)
        {
            if (!_chunkCache.TryGetValue(key, out var cached))
            {
                version = 0;
                encoded = [];
                return false;
            }

            _chunkCacheOrder.Remove(cached.Node);
            var node = _chunkCacheOrder.AddLast(key);
            _chunkCache[key] = (cached.Version, cached.Encoded, node);
            version = cached.Version;
            encoded = cached.Encoded;
            return true;
        }
    }

    private void CacheChunk((int X, int Z) key, int version, byte[] encoded)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_maxCachedChunks == 0)
            {
                return;
            }

            RemoveCachedChunkLocked(key);
            var node = _chunkCacheOrder.AddLast(key);
            _chunkCache[key] = (version, encoded, node);
            while (_chunkCache.Count > _maxCachedChunks)
            {
                var oldest = _chunkCacheOrder.First!;
                _chunkCacheOrder.RemoveFirst();
                _chunkCache.Remove(oldest.Value);
            }
        }
    }

    private void RemoveCachedChunkLocked((int X, int Z) key)
    {
        if (_chunkCache.Remove(key, out var cached))
        {
            _chunkCacheOrder.Remove(cached.Node);
        }
    }

    private void SaveRegionChunksLocked(
        int regionX,
        int regionZ,
        IReadOnlyList<(int X, int Z, PersistedChunk Persisted)> chunks)
    {
        var path = WorldFile.RegionPath(_directory, regionX, regionZ);
        var index = GetRegionIndexLocked(regionX, regionZ);
        if (index is not null && index.Version < WorldFile.RegionVersion)
        {
            index = UpgradeRegionLocked(index, regionX, regionZ);
        }

        var offsets = index is null ? new int[WorldFile.SlotsPerRegion] : (int[])index.Offsets.Clone();
        var lengths = index is null ? new int[WorldFile.SlotsPerRegion] : (int[])index.Lengths.Clone();
        var temporaryPath = path + TemporarySuffix;
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        if (File.Exists(path))
        {
            File.Copy(path, temporaryPath, overwrite: false);
        }

        if (!File.Exists(temporaryPath))
        {
            using var headerStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            WriteEmptyRegionHeader(headerStream);
        }

        var changedKeys = new List<(int X, int Z)>(chunks.Count);
        using (var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (stream.Length < WorldFile.RegionHeaderBytes)
            {
                throw new InvalidDataException($"region 文件头不完整: {path}");
            }

            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            stream.Seek(0, SeekOrigin.End);
            foreach (var entry in chunks.OrderBy(chunk => chunk.Z).ThenBy(chunk => chunk.X))
            {
                var payload = WorldFile.EncodeChunkPayload(
                    entry.X,
                    entry.Z,
                    entry.Persisted.Status,
                    entry.Persisted.TicketLevel,
                    entry.Persisted.Chunk,
                    WorldFile.RegionVersion);
                var offset = checked((int)stream.Position);
                writer.Write(payload);
                var slot = WorldFile.SlotIndexForStorage(regionX, regionZ, entry.X, entry.Z);
                offsets[slot] = offset;
                lengths[slot] = payload.Length;
                changedKeys.Add((entry.X, entry.Z));
            }

            writer.Flush();
            stream.Flush(flushToDisk: true);
            stream.Seek(WorldFile.RegionDirectoryOffset, SeekOrigin.Begin);
            for (var slot = 0; slot < WorldFile.SlotsPerRegion; slot++)
            {
                writer.Write(offsets[slot]);
                writer.Write(lengths[slot]);
            }

            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, path, overwrite: true);
        var updatedIndex = new RegionIndex(path, WorldFile.RegionVersion, offsets, lengths);
        _regions[(regionX, regionZ)] = ShouldCompact(updatedIndex)
            ? CompactRegionLocked(updatedIndex)
            : updatedIndex;
        foreach (var key in changedKeys)
        {
            RemoveCachedChunkLocked(key);
        }
    }

    private static bool ShouldCompact(RegionIndex index)
    {
        var activePayloadBytes = index.Lengths.Sum(length => (long)Math.Max(0, length));
        var fileBytes = new FileInfo(index.Path).Length;
        var staleBytes = fileBytes - WorldFile.RegionHeaderBytes - activePayloadBytes;
        var threshold = Math.Max(MinimumCompactionStaleBytes, activePayloadBytes / 4);
        return staleBytes >= threshold;
    }

    /// <summary>
    /// 只复制目录仍指向的最新负载，清理 SaveChunk 追加产生的旧负载。
    /// 使用同名 .tmp，沿用启动恢复逻辑保证进程中断后旧文件仍可读。
    /// </summary>
    private static RegionIndex CompactRegionLocked(RegionIndex index)
    {
        var temporaryPath = index.Path + TemporarySuffix;
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        var offsets = new int[WorldFile.SlotsPerRegion];
        var lengths = (int[])index.Lengths.Clone();
        using (var source = File.OpenRead(index.Path))
        using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            WriteEmptyRegionHeader(target);
            using var writer = new BinaryWriter(target, Encoding.UTF8, leaveOpen: true);
            for (var slot = 0; slot < WorldFile.SlotsPerRegion; slot++)
            {
                var length = index.Lengths[slot];
                if (length <= 0)
                {
                    continue;
                }

                var offset = index.Offsets[slot];
                if (offset < WorldFile.RegionHeaderBytes || offset > source.Length - length)
                {
                    throw new InvalidDataException($"region 压缩回收时目录项越界: {index.Path} slot={slot}");
                }

                source.Seek(offset, SeekOrigin.Begin);
                var payload = new byte[length];
                source.ReadExactly(payload);
                offsets[slot] = checked((int)target.Position);
                writer.Write(payload);
            }

            writer.Flush();
            target.Flush(flushToDisk: true);
            for (var slot = 0; slot < WorldFile.SlotsPerRegion; slot++)
            {
                target.Seek(WorldFile.RegionDirectoryOffset + slot * sizeof(int) * 2, SeekOrigin.Begin);
                writer.Write(offsets[slot]);
                writer.Write(lengths[slot]);
            }

            writer.Flush();
            target.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, index.Path, overwrite: true);
        return new RegionIndex(index.Path, index.Version, offsets, lengths);
    }

    /// <summary>把旧 4-Section region 转成当前 24-Section 负载，保留旧世界的绝对 Y=0..63。</summary>
    private static RegionIndex UpgradeRegionLocked(RegionIndex index, int regionX, int regionZ)
    {
        var temporaryPath = index.Path + TemporarySuffix;
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        var offsets = new int[WorldFile.SlotsPerRegion];
        var lengths = new int[WorldFile.SlotsPerRegion];
        using (var source = File.OpenRead(index.Path))
        using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            WriteEmptyRegionHeader(target);
            using var writer = new BinaryWriter(target, Encoding.UTF8, leaveOpen: true);
            for (var slot = 0; slot < WorldFile.SlotsPerRegion; slot++)
            {
                var length = index.Lengths[slot];
                if (length <= 0)
                {
                    continue;
                }

                var offset = index.Offsets[slot];
                if (offset < WorldFile.RegionHeaderBytes || offset > source.Length - length)
                {
                    throw new InvalidDataException($"region 升级时目录项越界: {index.Path} slot={slot}");
                }

                source.Seek(offset, SeekOrigin.Begin);
                var encoded = new byte[length];
                source.ReadExactly(encoded);
                var chunkX = regionX * WorldFile.RegionSize + slot % WorldFile.RegionSize;
                var chunkZ = regionZ * WorldFile.RegionSize + slot / WorldFile.RegionSize;
                var persisted = DecodeChunkPayload(encoded, index.Version, chunkX, chunkZ, index.Path, slot);
                var upgraded = WorldFile.EncodeChunkPayload(
                    chunkX,
                    chunkZ,
                    persisted.Status,
                    persisted.TicketLevel,
                    persisted.Chunk,
                    WorldFile.RegionVersion);
                offsets[slot] = checked((int)target.Position);
                lengths[slot] = upgraded.Length;
                writer.Write(upgraded);
            }

            writer.Flush();
            target.Flush(flushToDisk: true);
            for (var slot = 0; slot < WorldFile.SlotsPerRegion; slot++)
            {
                target.Seek(WorldFile.RegionDirectoryOffset + slot * sizeof(int) * 2, SeekOrigin.Begin);
                writer.Write(offsets[slot]);
                writer.Write(lengths[slot]);
            }

            writer.Flush();
            target.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, index.Path, overwrite: true);
        return new RegionIndex(index.Path, WorldFile.RegionVersion, offsets, lengths);
    }

    private static PersistedChunk DecodeChunkPayload(
        byte[] encoded,
        int version,
        int cx,
        int cz,
        string source,
        int slot)
    {
        using var payloadStream = new MemoryStream(WorldFile.DecodeChunkPayload(encoded, version), writable: false);
        using var payloadReader = new BinaryReader(payloadStream);
        var payload = WorldFile.ReadChunkPayload(payloadReader, version);
        if (payload.X != cx || payload.Z != cz)
        {
            throw new InvalidDataException(
                $"region 区块坐标与目录不一致: requested=({cx},{cz}) stored=({payload.X},{payload.Z}) source={source} slot={slot}");
        }

        return payload.Data;
    }

    private RegionIndex? GetRegionIndex(int regionX, int regionZ)
    {
        lock (_sync)
        {
            return GetRegionIndexLocked(regionX, regionZ);
        }
    }

    private RegionIndex? GetRegionIndexLocked(int regionX, int regionZ)
    {
        var key = (regionX, regionZ);
        var path = WorldFile.RegionPath(_directory, regionX, regionZ);
        if (_regions.TryGetValue(key, out var cached) && (cached is not null || !File.Exists(path)))
        {
            return cached;
        }

        var loaded = File.Exists(path) ? ReadRegionIndex(path) : null;
        _regions[key] = loaded;
        return loaded;
    }

    private void RecoverTemporaryRegions()
    {
        foreach (var temporaryPath in Directory.EnumerateFiles(_directory, "*.cubitregion.tmp"))
        {
            var path = temporaryPath[..^TemporarySuffix.Length];
            var temporaryValid = TryValidateRegionFile(temporaryPath);
            if (!temporaryValid)
            {
                File.Delete(temporaryPath);
                continue;
            }

            if (File.Exists(path) && TryValidateRegionFile(path))
            {
                File.Delete(temporaryPath);
                continue;
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
    }

    private static bool TryValidateRegionFile(string path)
    {
        try
        {
            _ = ReadRegionIndex(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void WriteEmptyRegionHeader(FileStream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(WorldFile.RegionMagic);
        writer.Write(WorldFile.RegionVersion);
        for (var slot = 0; slot < WorldFile.SlotsPerRegion; slot++)
        {
            writer.Write(0);
            writer.Write(0);
        }

        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static RegionIndex ReadRegionIndex(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        var magic = reader.ReadString();
        if (magic != WorldFile.RegionMagic)
        {
            throw new InvalidDataException($"不是 Cubit region 文件: {path}");
        }

        var version = reader.ReadInt32();
        if (version < WorldFile.LegacyRegionVersion || version > WorldFile.RegionVersion)
        {
            throw new InvalidDataException($"region 版本 {version} 不支持（支持 {WorldFile.LegacyRegionVersion}..{WorldFile.RegionVersion}）");
        }

        var offsets = new int[WorldFile.SlotsPerRegion];
        var lengths = new int[WorldFile.SlotsPerRegion];
        for (var slot = 0; slot < WorldFile.SlotsPerRegion; slot++)
        {
            offsets[slot] = reader.ReadInt32();
            lengths[slot] = reader.ReadInt32();
            if (lengths[slot] < 0)
            {
                throw new InvalidDataException($"region 区块长度为负数: {path} slot={slot}");
            }
        }

        if (stream.Length < WorldFile.RegionHeaderBytes)
        {
            throw new InvalidDataException($"region 文件头不完整: {path}");
        }

        for (var slot = 0; slot < WorldFile.SlotsPerRegion; slot++)
        {
            var offset = offsets[slot];
            var length = lengths[slot];
            if (length > 0 && (offset < WorldFile.RegionHeaderBytes || offset > stream.Length - length))
            {
                throw new InvalidDataException($"region 区块目录项越界: {path} slot={slot}");
            }
        }

        return new RegionIndex(path, version, offsets, lengths);
    }
}
