using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Cubit.Voxel.World;

/// <summary>
/// 世界文件（M9）：region 分片存档（借鉴 MC Anvil 思路）。
/// - 世界目录：level.json（版本/种子/类型）+ r.&lt;rx&gt;.&lt;rz&gt;.cubitregion（32×32 区块/文件）
/// - region 文件：头（magic+version+1024×目录项 offset/length）+ 顺序区块负载
/// - 版本化 + 迁移：旧版单文件（v2，CUBITW）与旧 4-Section region 可迁移到当前 24-Section region 目录
/// </summary>
public static class WorldFile
{
    public const int Version = 4; // v4：原版高度 24 Section
    public const string RegionMagic = "CUBITRG";
    public const int LegacyRegionVersion = 1;
    public const int CompressedRegionVersion = 2;
    public const int CrcRegionVersion = 3;
    public const int HeightRegionVersion = 4;
    public const int RegionVersion = HeightRegionVersion;
    private const byte CompressionNone = 0;
    private const byte CompressionDeflate = 1;
    public const int RegionSize = 32;
    public const int SlotsPerRegion = RegionSize * RegionSize;
    public const int RegionDirectoryOffset = 1 + 7 + sizeof(int);
    public const int RegionHeaderBytes = 1 + 7 + sizeof(int) + SlotsPerRegion * sizeof(int) * 2;

    public const string LegacyMagic = "CUBITW";
    public const int LegacyVersion = 2; // v2：单文件（含光照）

    // ---- region 坐标 ----

    public static (int RegionX, int RegionZ) ChunkToRegion(int cx, int cz) =>
        ((int)MathF.Floor((float)cx / RegionSize), (int)MathF.Floor((float)cz / RegionSize));

    public static string RegionFileName(int rx, int rz) => $"r.{rx}.{rz}.cubitregion";

    public static string RegionPath(string directory, int rx, int rz) => Path.Combine(directory, RegionFileName(rx, rz));

    // ---- 保存 ----

    public static void Save(VoxelWorld world, string directory)
    {
        Directory.CreateDirectory(directory);

        WriteLevelMetadata(world, directory);

        var byRegion = world.Store.ChunkKeys
            .GroupBy(k => ChunkToRegion(k.X, k.Z))
            .ToArray();
        foreach (var group in byRegion)
        {
            using var stream = File.Create(RegionPath(directory, group.Key.RegionX, group.Key.RegionZ));
            using var writer = new BinaryWriter(stream);
            writer.Write(RegionMagic);
            writer.Write(RegionVersion);

            var directoryOffset = (int)stream.Position;
            for (var i = 0; i < SlotsPerRegion; i++)
            {
                writer.Write(0); // offset
                writer.Write(0); // length
            }

            foreach (var (cx, cz) in group)
            {
                var chunk = world.Store.GetChunk(cx, cz);
                if (chunk is null)
                {
                    continue;
                }

                var slot = SlotIndex(group.Key.RegionX, group.Key.RegionZ, cx, cz);
                var payload = EncodeChunkPayload(
                    cx,
                    cz,
                    world.Store.GetStatus(cx, cz),
                    world.Store.GetTicketLevel(cx, cz),
                    chunk,
                    RegionVersion);
                var offset = (int)stream.Position;
                writer.Write(payload);
                var length = payload.Length;

                var entry = directoryOffset + slot * 8;
                stream.Seek(entry, SeekOrigin.Begin);
                writer.Write(offset);
                writer.Write(length);
                stream.Seek(0, SeekOrigin.End);
            }
        }

        world.ClearDirtyChunks(world.DirtyChunkKeys);
    }

    /// <summary>只保存已经标记为脏的区块，未加载的远端区块不会被覆盖。</summary>
    public static void SaveDirty(VoxelWorld world, WorldFileStorage storage)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(storage);
        SaveDirty(world, storage, world.DirtyChunkKeys);
    }

    /// <summary>只保存指定集合内仍为脏状态的已加载区块，供流式卸载分帧持久化。</summary>
    internal static void SaveDirty(
        VoxelWorld world,
        WorldFileStorage storage,
        IEnumerable<(int X, int Z)> keys)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(keys);

        var dirtyKeys = world.DirtyChunkKeys.ToHashSet();
        var dirty = keys
            .Where(dirtyKeys.Contains)
            .Distinct()
            .OrderBy(key => key.Z)
            .ThenBy(key => key.X)
            .ToArray();
        if (dirty.Length == 0)
        {
            return;
        }

        WriteLevelMetadata(world, storage.DirectoryPath);
        var writes = new List<(int X, int Z, PersistedChunk Persisted)>(dirty.Length);
        foreach (var (cx, cz) in dirty)
        {
            var chunk = world.Store.GetChunk(cx, cz);
            if (chunk is null)
            {
                continue;
            }

            writes.Add((
                cx,
                cz,
                new PersistedChunk(
                    chunk,
                    world.Store.GetStatus(cx, cz),
                    world.Store.GetTicketLevel(cx, cz))));
        }

        storage.SaveChunks(writes);

        world.ClearDirtyChunks(writes.Select(write => (write.X, write.Z)));
    }

    private static void WriteLevelMetadata(VoxelWorld world, string directory)
    {
        Directory.CreateDirectory(directory);
        var level = new
        {
            version = Version,
            seed = world.Seed,
            worldType = world.WorldType.ToString(),
            contentHash = BlockRegistry.ContentHash,
            fluidActivations = world.FluidActivationCoordinates
                .Select(coordinate => new[] { coordinate.X, coordinate.Y, coordinate.Z })
                .ToArray(),
        };
        File.WriteAllText(Path.Combine(directory, "level.json"), JsonSerializer.Serialize(level));
    }

    internal static void WriteChunkPayload(BinaryWriter writer, int cx, int cz, ChunkStatus status, TicketLevel ticket, Chunk chunk) =>
        WriteChunkPayload(writer, cx, cz, status, ticket, chunk, Chunk.SectionCount);

    internal static void WriteChunkPayload(
        BinaryWriter writer,
        int cx,
        int cz,
        ChunkStatus status,
        TicketLevel ticket,
        Chunk chunk,
        int sectionCount)
    {
        writer.Write(cx);
        writer.Write(cz);
        writer.Write((int)status);
        writer.Write((int)ticket);
        if (sectionCount is not (Chunk.LegacySectionCount or Chunk.SectionCount))
        {
            throw new ArgumentOutOfRangeException(nameof(sectionCount), "区块负载 Section 数量必须是旧版 4 或当前 24");
        }

        var firstSection = sectionCount == Chunk.LegacySectionCount ? Chunk.SectionIndex(0) : 0;
        for (var s = 0; s < sectionCount; s++)
        {
            chunk.GetSection(firstSection + s).Save(writer);
        }
    }

    internal static byte[] EncodeChunkPayload(
        int cx,
        int cz,
        ChunkStatus status,
        TicketLevel ticket,
        Chunk chunk,
        int regionVersion)
    {
        using var rawStream = new MemoryStream();
        using (var rawWriter = new BinaryWriter(rawStream, Encoding.UTF8, leaveOpen: true))
        {
            var sectionCount = regionVersion >= HeightRegionVersion ? Chunk.SectionCount : Chunk.LegacySectionCount;
            WriteChunkPayload(rawWriter, cx, cz, status, ticket, chunk, sectionCount);
            rawWriter.Flush();
        }

        var raw = rawStream.ToArray();
        if (regionVersion < CompressedRegionVersion)
        {
            return raw;
        }

        using var compressedStream = new MemoryStream();
        using (var deflate = new DeflateStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        var compressed = compressedStream.ToArray();
        var useCompression = compressed.Length < raw.Length;
        using var encodedStream = new MemoryStream();
        using (var writer = new BinaryWriter(encodedStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(useCompression ? CompressionDeflate : CompressionNone);
            writer.Write(raw.Length);
            if (regionVersion >= CrcRegionVersion)
            {
                writer.Write(ComputeCrc32(raw));
            }
            writer.Write(useCompression ? compressed : raw);
            writer.Flush();
        }

        return encodedStream.ToArray();
    }

    internal static byte[] DecodeChunkPayload(byte[] encoded, int regionVersion)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (regionVersion < CompressedRegionVersion)
        {
            return encoded;
        }

        using var encodedStream = new MemoryStream(encoded, writable: false);
        using var reader = new BinaryReader(encodedStream, Encoding.UTF8, leaveOpen: true);
        var compression = reader.ReadByte();
        var rawLength = reader.ReadInt32();
        if (rawLength < 0)
        {
            throw new InvalidDataException("区块负载原始长度不能为负数");
        }

        var expectedCrc = regionVersion >= CrcRegionVersion ? reader.ReadUInt32() : 0u;
        var payload = reader.ReadBytes((int)(encodedStream.Length - encodedStream.Position));
        if (compression == CompressionNone)
        {
            if (payload.Length != rawLength)
            {
                throw new InvalidDataException("未压缩区块负载长度不一致");
            }

            if (regionVersion >= CrcRegionVersion && ComputeCrc32(payload) != expectedCrc)
            {
                throw new InvalidDataException("区块负载 CRC32 校验失败");
            }

            return payload;
        }

        if (compression != CompressionDeflate)
        {
            throw new InvalidDataException($"未知区块压缩类型: {compression}");
        }

        using var compressedStream = new MemoryStream(payload, writable: false);
        using var deflate = new DeflateStream(compressedStream, CompressionMode.Decompress);
        using var rawStream = new MemoryStream(rawLength);
        deflate.CopyTo(rawStream);
        var raw = rawStream.ToArray();
        if (raw.Length != rawLength)
        {
            throw new InvalidDataException("压缩区块负载解压长度不一致");
        }

        if (regionVersion >= CrcRegionVersion && ComputeCrc32(raw) != expectedCrc)
        {
            throw new InvalidDataException("区块负载 CRC32 校验失败");
        }

        return raw;
    }

    internal static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }

    internal static (int X, int Z, PersistedChunk Data) ReadChunkPayload(BinaryReader reader, int regionVersion)
    {
        var cx = reader.ReadInt32();
        var cz = reader.ReadInt32();
        var status = (ChunkStatus)reader.ReadInt32();
        var ticket = (TicketLevel)reader.ReadInt32();
        var sectionCount = regionVersion >= HeightRegionVersion ? Chunk.SectionCount : Chunk.LegacySectionCount;
        var firstSection = sectionCount == Chunk.LegacySectionCount ? Chunk.SectionIndex(0) : 0;
        var chunk = new Chunk();
        for (var section = 0; section < sectionCount; section++)
        {
            chunk.SetSection(firstSection + section, Section.Load(reader));
        }

        return (cx, cz, new PersistedChunk(chunk, status, ticket));
    }

    internal static (int X, int Z, PersistedChunk Data) ReadChunkPayload(BinaryReader reader) =>
        ReadChunkPayload(reader, RegionVersion);

    private static int SlotIndex(int rx, int rz, int cx, int cz)
    {
        var lx = cx - rx * RegionSize;
        var lz = cz - rz * RegionSize;
        return lz * RegionSize + lx;
    }

    internal static int SlotIndexForStorage(int rx, int rz, int cx, int cz) => SlotIndex(rx, rz, cx, cz);

    // ---- 加载 ----

    /// <summary>只读取世界元数据，不扫描或加载任何 region 区块。</summary>
    public static VoxelWorld LoadMetadata(
        string path,
        Func<string, bool>? additionalContentHashCompatibility = null,
        VoxelFluidSimulationOptions? fluidOptions = null)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"找不到世界存档目录: {path}");
        }

        var levelJson = File.ReadAllText(Path.Combine(path, "level.json"));
        using var levelDoc = JsonDocument.Parse(levelJson);
        var levelRoot = levelDoc.RootElement;
        var version = levelRoot.GetProperty("version").GetInt32();
        if (version > Version)
        {
            throw new InvalidDataException($"世界版本 {version} 高于当前 {Version}");
        }

        if (levelRoot.TryGetProperty("contentHash", out var contentHashElement) &&
            contentHashElement.ValueKind == JsonValueKind.String)
        {
            var savedHash = contentHashElement.GetString();
            if (!string.IsNullOrWhiteSpace(savedHash) &&
                !string.Equals(savedHash, BlockRegistry.ContentHash, StringComparison.OrdinalIgnoreCase) &&
                !(additionalContentHashCompatibility?.Invoke(savedHash) ?? false))
            {
                throw new InvalidDataException($"世界内容包不匹配: 存档={savedHash} 当前={BlockRegistry.ContentHash}");
            }
        }

        var seed = levelRoot.GetProperty("seed").GetUInt32();
        var worldType = Enum.TryParse<WorldType>(levelRoot.GetProperty("worldType").GetString(), out var wt)
            ? wt
            : WorldType.Noise;
        var world = new VoxelWorld(seed, worldType, fluidOptions: fluidOptions);
        if (levelRoot.TryGetProperty("fluidActivations", out var activations) &&
            activations.ValueKind == JsonValueKind.Array)
        {
            var coordinates = new List<(int X, int Y, int Z)>();
            foreach (var entry in activations.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() != 3)
                {
                    throw new InvalidDataException("流体激活坐标必须是 [x,y,z] 数组");
                }

                coordinates.Add((entry[0].GetInt32(), entry[1].GetInt32(), entry[2].GetInt32()));
            }

            if (coordinates.Count > world.FluidSimulationOptions.MaxQueuedUpdates)
            {
                throw new InvalidDataException($"流体激活坐标数量超过上限: {coordinates.Count}");
            }

            world.RestoreFluidActivations(coordinates);
        }

        return world;
    }

    public static VoxelWorld Load(
        string path,
        Func<string, bool>? additionalContentHashCompatibility = null,
        VoxelFluidSimulationOptions? fluidOptions = null)
    {
        if (File.Exists(path) && !Directory.Exists(path))
        {
            // 旧版单文件：迁移加载（DataFixer 路径）
            var migrated = LoadLegacy(path);
            var tempDir = Path.Combine(Path.GetTempPath(), $"cubit_migrate_{Guid.NewGuid():N}");
            try
            {
                Save(migrated, tempDir);
                var loaded = Load(tempDir, fluidOptions: fluidOptions);
                return loaded;
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        var world = LoadMetadata(path, additionalContentHashCompatibility, fluidOptions);

        foreach (var file in Directory.EnumerateFiles(path, "*.cubitregion"))
        {
            LoadRegion(world, file);
        }

        return world;
    }

    private static void LoadRegion(VoxelWorld world, string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        var magic = reader.ReadString();
        if (magic != RegionMagic)
        {
            throw new InvalidDataException($"不是 Cubit region 文件: {path}");
        }

        var version = reader.ReadInt32();
        if (version < LegacyRegionVersion || version > RegionVersion)
        {
            throw new InvalidDataException($"region 版本 {version} 不支持（支持 {LegacyRegionVersion}..{RegionVersion}）");
        }

        var directoryStart = (int)stream.Position;
        for (var slot = 0; slot < SlotsPerRegion; slot++)
        {
            var offset = reader.ReadInt32();
            var length = reader.ReadInt32();
            if (length <= 0)
            {
                continue;
            }

            stream.Seek(offset, SeekOrigin.Begin);
            var encoded = reader.ReadBytes(length);
            if (encoded.Length != length)
            {
                throw new InvalidDataException($"region 区块负载截断: {path} slot={slot}");
            }

            using var payloadStream = new MemoryStream(DecodeChunkPayload(encoded, version), writable: false);
            using var payloadReader = new BinaryReader(payloadStream);
            var payload = ReadChunkPayload(payloadReader, version);
            if (!world.CommitLoadedChunk(payload.X, payload.Z, payload.Data))
            {
                throw new InvalidDataException($"region 中存在重复区块坐标: ({payload.X},{payload.Z})");
            }
            stream.Seek(directoryStart + (slot + 1) * 8, SeekOrigin.Begin);
        }
    }

    // ---- 旧版单文件（v2）与迁移 ----

    public static void SaveLegacy(VoxelWorld world, string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write(LegacyMagic);
        writer.Write(LegacyVersion);
        writer.Write(world.Seed);
        writer.Write((int)world.WorldType);

        var keys = world.Store.ChunkKeys;
        writer.Write(keys.Length);
        foreach (var (cx, cz) in keys)
        {
            var chunk = world.Store.GetChunk(cx, cz);
            if (chunk is null)
            {
                throw new InvalidDataException($"区块 ({cx},{cz}) 在快照后消失");
            }

            WriteChunkPayload(
                writer,
                cx,
                cz,
                world.Store.GetStatus(cx, cz),
                world.Store.GetTicketLevel(cx, cz),
                chunk,
                Chunk.LegacySectionCount);
        }
    }

    public static VoxelWorld LoadLegacy(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadString();
        if (magic != LegacyMagic)
        {
            throw new InvalidDataException($"不是 Cubit 旧版世界文件（magic={magic}）");
        }

        var version = reader.ReadInt32();
        if (version > LegacyVersion)
        {
            throw new InvalidDataException($"旧版文件版本 {version} 不支持");
        }

        var seed = reader.ReadUInt32();
        var worldType = (WorldType)reader.ReadInt32();
        var world = new VoxelWorld(seed, worldType);

        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var cx = reader.ReadInt32();
            var cz = reader.ReadInt32();
            var status = (ChunkStatus)reader.ReadInt32();
            var ticket = (TicketLevel)reader.ReadInt32();
            var chunk = new Chunk();
            for (var s = 0; s < Chunk.LegacySectionCount; s++)
            {
                chunk.SetSection(Chunk.SectionIndex(0) + s, Section.Load(reader));
            }

            if (!world.CommitLoadedChunk(cx, cz, new PersistedChunk(chunk, status, ticket)))
            {
                throw new InvalidDataException($"旧版世界中存在重复区块坐标: ({cx},{cz})");
            }
        }

        return world;
    }

    /// <summary>迁移旧版单文件到 region 目录（DataFixer 语义：v2 → v3）。</summary>
    public static void MigrateLegacyFile(string legacyPath, string directory)
    {
        var world = LoadLegacy(legacyPath);
        Save(world, directory);
    }
}
