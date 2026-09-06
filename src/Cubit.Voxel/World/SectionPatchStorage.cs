using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace Cubit.Voxel.World;

/// <summary>
/// 可选的 Section 增量记录实验后端。
/// v4 region 作为完整基线，编辑只把 dirty Section 追加到同 region 的 .cubitsectionlog；
/// 默认项目仍使用 WorldFileStorage，不会隐式切换存档格式。
/// </summary>
public sealed class SectionPatchStorage : IChunkStorageWriter, IDisposable
{
    public const string PatchMagic = "CUBITSP";
    public const int PatchVersion = 1;
    private const int PatchHeaderBytes = 1 + 7 + sizeof(int);
    private const int PatchRecordFixedBytes = sizeof(int) * 4 + sizeof(byte) + sizeof(int) + sizeof(uint) + sizeof(int);
    private const int MinimumCompactionStaleBytes = 16 * 1024;
    private const string TemporarySuffix = ".tmp";
    private const byte MetadataSectionIndex = byte.MaxValue;
    private readonly WorldFileStorage _baseStorage;
    private readonly string _directory;
    private readonly Dictionary<(int X, int Z), PatchRegionState?> _regions = [];
    private readonly object _sync = new();
    private bool _disposed;

    private sealed class PatchRegionState
    {
        public long Length { get; init; }

        public long ValidLength { get; set; }

        public int RecordCount { get; set; }

        public Dictionary<(int X, int Z), PatchChunkState> Chunks { get; } = [];
    }

    private sealed class PatchChunkState
    {
        public bool HasMetadata { get; set; }

        public ChunkStatus Status { get; set; }

        public TicketLevel TicketLevel { get; set; }

        public Dictionary<int, SectionPatchPayload> Sections { get; } = [];
    }

    private readonly record struct SectionPatchPayload(byte[] Compressed, int RawLength, uint Crc);

    public SectionPatchStorage(string directory, int maxCachedChunks = 0)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("世界存档目录不能为空", nameof(directory));
        }

        _directory = Path.GetFullPath(directory);
        RecoverTemporaryPatches();
        _baseStorage = new WorldFileStorage(_directory, maxCachedChunks);
    }

    public string DirectoryPath => _directory;

    public static string PatchPath(string directory, int regionX, int regionZ) =>
        Path.Combine(directory, $"r.{regionX}.{regionZ}.cubitsectionlog");

    public bool TryLoadChunk(int cx, int cz, out PersistedChunk chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (!_baseStorage.TryLoadChunk(cx, cz, out chunk))
            {
                return false;
            }

            var (regionX, regionZ) = WorldFile.ChunkToRegion(cx, cz);
            var region = LoadRegionLocked(regionX, regionZ);
            if (region is null || !region.Chunks.TryGetValue((cx, cz), out var patch))
            {
                return true;
            }

            foreach (var (sectionIndex, payload) in patch.Sections)
            {
                chunk.Chunk.SetSection(sectionIndex, DecodeSection(payload));
            }

            var status = patch.HasMetadata ? patch.Status : chunk.Status;
            var ticket = patch.HasMetadata ? patch.TicketLevel : chunk.TicketLevel;
            chunk = new PersistedChunk(chunk.Chunk, status, ticket);
            return true;
        }
    }

    public void SaveChunk(int cx, int cz, PersistedChunk persisted)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(persisted.Chunk);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_baseStorage.TryLoadChunk(cx, cz, out var baseChunk))
            {
                // 没有完整基线时先走 v4 全区块写入，后续编辑再进入 Section 增量路径。
                _baseStorage.SaveChunk(cx, cz, persisted);
                return;
            }

            var (regionX, regionZ) = WorldFile.ChunkToRegion(cx, cz);
            var region = LoadRegionLocked(regionX, regionZ);
            PatchChunkState? patchState = null;
            var hasPatchState = region is not null && region.Chunks.TryGetValue((cx, cz), out patchState);
            var currentStatus = hasPatchState && patchState is not null && patchState.HasMetadata
                ? patchState.Status
                : baseChunk.Status;
            var currentTicket = hasPatchState && patchState is not null && patchState.HasMetadata
                ? patchState.TicketLevel
                : baseChunk.TicketLevel;
            var mask = persisted.Chunk.DirtySectionMask;
            var metadataChanged = persisted.Status != currentStatus || persisted.TicketLevel != currentTicket;
            if (mask == 0 && !metadataChanged)
            {
                return;
            }

            var records = new List<byte[]>(BitOperations.PopCount(mask));
            for (var sectionIndex = 0; sectionIndex < Chunk.SectionCount; sectionIndex++)
            {
                if ((mask & (1UL << sectionIndex)) == 0)
                {
                    continue;
                }

                records.Add(EncodeRecord(cx, cz, persisted, sectionIndex));
            }

            if (mask == 0 && metadataChanged)
            {
                records.Add(EncodeMetadataRecord(cx, cz, persisted.Status, persisted.TicketLevel));
            }

            AppendRecords(regionX, regionZ, records);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            _regions.Clear();
            _baseStorage.Dispose();
        }
    }

    private byte[] EncodeRecord(int cx, int cz, PersistedChunk persisted, int sectionIndex)
    {
        var section = persisted.Chunk.GetSection(sectionIndex);
        using var rawStream = new MemoryStream();
        using (var rawWriter = new BinaryWriter(rawStream, Encoding.UTF8, leaveOpen: true))
        {
            section.Save(rawWriter);
            rawWriter.Flush();
        }

        var raw = rawStream.ToArray();
        using var compressedStream = new MemoryStream();
        using (var deflate = new DeflateStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        var compressed = compressedStream.ToArray();
        return EncodeRecordPayload(
            cx,
            cz,
            persisted.Status,
            persisted.TicketLevel,
            sectionIndex,
            new SectionPatchPayload(compressed, raw.Length, WorldFile.ComputeCrc32(raw)));
    }

    private static byte[] EncodeRecordPayload(
        int cx,
        int cz,
        ChunkStatus status,
        TicketLevel ticket,
        int sectionIndex,
        SectionPatchPayload payload)
    {
        using var recordStream = new MemoryStream();
        using (var writer = new BinaryWriter(recordStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(cx);
            writer.Write(cz);
            writer.Write((int)status);
            writer.Write((int)ticket);
            writer.Write((byte)sectionIndex);
            writer.Write(payload.RawLength);
            writer.Write(payload.Crc);
            writer.Write(payload.Compressed.Length);
            writer.Write(payload.Compressed);
            writer.Flush();
        }

        return recordStream.ToArray();
    }

    private static byte[] EncodeMetadataRecord(int cx, int cz, ChunkStatus status, TicketLevel ticket)
    {
        using var recordStream = new MemoryStream();
        using (var writer = new BinaryWriter(recordStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(cx);
            writer.Write(cz);
            writer.Write((int)status);
            writer.Write((int)ticket);
            writer.Write(MetadataSectionIndex);
            writer.Write(0); // 无 Section 原始负载
            writer.Write(0u); // 无 Section CRC
            writer.Write(0); // 无 Section 压缩负载
            writer.Flush();
        }

        return recordStream.ToArray();
    }

    private void AppendRecords(int regionX, int regionZ, IReadOnlyList<byte[]> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        var path = PatchPath(_directory, regionX, regionZ);
        var existing = LoadRegionLocked(regionX, regionZ);
        using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            if (existing is not null && existing.ValidLength < stream.Length)
            {
                // 上次进程在记录中途退出时，后续追加必须覆盖不完整尾部，不能把有效记录写在坏尾之后。
                stream.SetLength(existing.ValidLength);
            }

            stream.Seek(0, SeekOrigin.End);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            if (stream.Position == 0)
            {
                writer.Write(PatchMagic);
                writer.Write(PatchVersion);
            }

            foreach (var record in records)
            {
                writer.Write(record.Length);
                writer.Write(record);
            }

            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        // 写入句柄必须完全释放后才能在 Windows 上重新读取并判断 compaction。
        _regions.Remove((regionX, regionZ));
        var updated = LoadRegionLocked(regionX, regionZ);
        if (updated is not null && ShouldCompact(updated))
        {
            CompactRegionLocked(regionX, regionZ, updated);
        }
    }

    private static bool ShouldCompact(PatchRegionState state)
    {
        var activeRecordCount = 0;
        var activeBytes = PatchHeaderBytes;
        foreach (var chunk in state.Chunks.Values)
        {
            activeRecordCount += chunk.Sections.Count;
            if (chunk.Sections.Count == 0 && chunk.HasMetadata)
            {
                activeRecordCount++;
                activeBytes += sizeof(int) + PatchRecordFixedBytes;
            }

            foreach (var payload in chunk.Sections.Values)
            {
                activeBytes += sizeof(int) + PatchRecordFixedBytes + payload.Compressed.Length;
            }
        }

        var staleBytes = state.ValidLength - activeBytes;
        return state.RecordCount > activeRecordCount * 2 && staleBytes >= MinimumCompactionStaleBytes;
    }

    private void CompactRegionLocked(int regionX, int regionZ, PatchRegionState state)
    {
        var path = PatchPath(_directory, regionX, regionZ);
        var temporaryPath = path + TemporarySuffix;
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(PatchMagic);
            writer.Write(PatchVersion);
            foreach (var (key, chunk) in state.Chunks)
            {
                if (chunk.Sections.Count == 0 && chunk.HasMetadata)
                {
                    var metadata = EncodeMetadataRecord(key.X, key.Z, chunk.Status, chunk.TicketLevel);
                    writer.Write(metadata.Length);
                    writer.Write(metadata);
                    continue;
                }

                foreach (var (sectionIndex, payload) in chunk.Sections)
                {
                    var record = EncodeRecordPayload(
                        key.X,
                        key.Z,
                        chunk.Status,
                        chunk.TicketLevel,
                        sectionIndex,
                        payload);
                    writer.Write(record.Length);
                    writer.Write(record);
                }
            }

            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, path, overwrite: true);
        _regions.Remove((regionX, regionZ));
    }

    private void RecoverTemporaryPatches()
    {
        foreach (var temporaryPath in Directory.EnumerateFiles(_directory, $"*{TemporarySuffix}", SearchOption.TopDirectoryOnly))
        {
            if (!temporaryPath.EndsWith(".cubitsectionlog.tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var formalPath = temporaryPath[..^TemporarySuffix.Length];
            if (File.Exists(formalPath))
            {
                File.Delete(temporaryPath);
            }
            else
            {
                File.Move(temporaryPath, formalPath);
            }
        }
    }

    private PatchRegionState? LoadRegionLocked(int regionX, int regionZ)
    {
        var key = (regionX, regionZ);
        if (_regions.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var path = PatchPath(_directory, regionX, regionZ);
        if (!File.Exists(path))
        {
            _regions[key] = null;
            return null;
        }

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (stream.Length < PatchHeaderBytes || reader.ReadString() != PatchMagic || reader.ReadInt32() != PatchVersion)
        {
            throw new InvalidDataException($"Section patch 文件头无效: {path}");
        }

        var result = new PatchRegionState { Length = stream.Length, ValidLength = stream.Position };
        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < sizeof(int))
            {
                break; // 崩溃写入留下的不完整长度字段
            }

            var recordLength = reader.ReadInt32();
            if (recordLength <= 0 || recordLength > stream.Length - stream.Position)
            {
                break; // 只忽略最后一条不完整记录
            }

            var record = reader.ReadBytes(recordLength);
            if (record.Length != recordLength)
            {
                break;
            }

            ParseRecord(result, record, path);
            result.ValidLength = stream.Position;
            result.RecordCount++;
        }

        _regions[key] = result;
        return result;
    }

    private static void ParseRecord(PatchRegionState region, byte[] record, string path)
    {
        using var stream = new MemoryStream(record, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (record.Length < sizeof(int) * 4 + sizeof(byte) + sizeof(uint) * 2)
        {
            throw new InvalidDataException($"Section patch 记录过短: {path}");
        }

        var cx = reader.ReadInt32();
        var cz = reader.ReadInt32();
        var status = (ChunkStatus)reader.ReadInt32();
        var ticket = (TicketLevel)reader.ReadInt32();
        var sectionIndex = reader.ReadByte();
        var rawLength = reader.ReadInt32();
        var crc = reader.ReadUInt32();
        var compressedLength = reader.ReadInt32();
        if (sectionIndex == MetadataSectionIndex)
        {
            if (rawLength != 0 || crc != 0 || compressedLength != 0 || stream.Position != stream.Length)
            {
                throw new InvalidDataException($"Section patch 元数据记录字段无效: {path}");
            }

            var metadataKey = (cx, cz);
            if (!region.Chunks.TryGetValue(metadataKey, out var metadataChunk))
            {
                metadataChunk = new PatchChunkState();
                region.Chunks[metadataKey] = metadataChunk;
            }

            metadataChunk.HasMetadata = true;
            metadataChunk.Status = status;
            metadataChunk.TicketLevel = ticket;
            return;
        }

        if (sectionIndex >= Chunk.SectionCount || rawLength <= 0 || compressedLength <= 0 ||
            compressedLength != stream.Length - stream.Position)
        {
            throw new InvalidDataException($"Section patch 记录字段无效: {path}");
        }

        var compressed = reader.ReadBytes(compressedLength);
        var key = (cx, cz);
        if (!region.Chunks.TryGetValue(key, out var chunk))
        {
            chunk = new PatchChunkState();
            region.Chunks[key] = chunk;
        }

        chunk.HasMetadata = true;
        chunk.Status = status;
        chunk.TicketLevel = ticket;
        chunk.Sections[sectionIndex] = new SectionPatchPayload(compressed, rawLength, crc);
    }

    private static Section DecodeSection(SectionPatchPayload payload)
    {
        using var compressedStream = new MemoryStream(payload.Compressed, writable: false);
        using var deflate = new DeflateStream(compressedStream, CompressionMode.Decompress);
        using var rawStream = new MemoryStream(payload.RawLength);
        deflate.CopyTo(rawStream);
        var raw = rawStream.ToArray();
        if (raw.Length != payload.RawLength || WorldFile.ComputeCrc32(raw) != payload.Crc)
        {
            throw new InvalidDataException("Section patch 负载 CRC32 校验失败");
        }

        using var sectionStream = new MemoryStream(raw, writable: false);
        using var reader = new BinaryReader(sectionStream, Encoding.UTF8, leaveOpen: true);
        return Section.Load(reader);
    }
}
