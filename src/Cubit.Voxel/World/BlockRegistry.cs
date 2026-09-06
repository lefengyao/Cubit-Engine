using System.Security.Cryptography;
using System.Text;
using Cubit.Core.Scene;
using Cubit.Voxel.Textures;

namespace Cubit.Voxel.World;

/// <summary>
/// 方块注册表：内置方块固定 ID（0~7），项目内容包按稳定 Key 追加。
/// 注册顺序确定性：内置 Bootstrap → 调用方显式加载内容包。
/// </summary>
public static class BlockRegistry
{
    public const ushort Air = 0;
    public const ushort Grass = 1;
    public const ushort Dirt = 2;
    public const ushort Stone = 3;
    public const ushort Sand = 4;
    public const ushort Wood = 5;
    public const ushort Leaves = 6;
    public const ushort Bedrock = 7;

    private static readonly List<BlockDef> Blocks = [];
    private static readonly Dictionary<string, ushort> IdsByKey = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoadedContentPacks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> LoadedContentPackHashes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> LoadedContentPackIdMapSignatures = new(StringComparer.OrdinalIgnoreCase);
    private static int _nextAutoId = 8;

    /// <summary>当前已注册方块定义的确定性 SHA-256 摘要，用于存档内容兼容检查。</summary>
    public static string ContentHash => ComputeContentHash();

    /// <summary>当前会话已注册的真实方块定义；占位空气不对外暴露。</summary>
    public static IReadOnlyList<BlockDef> Definitions => Blocks
        .Where(def => def is not null && def.Id < Blocks.Count && def.Key.Length > 0)
        .ToArray();

    static BlockRegistry()
    {
        // 0. 空气（占位）
        Register(new BlockDef { Id = Air, Key = "cubit.voxel:air", Name = "空气", Solid = false, Opaque = false });

        // 1. 草方块：顶=草皮，侧=草侧，底=泥土
        Register(new BlockDef
        {
            Id = Grass,
            Key = "cubit.voxel:grass",
            Name = "草方块",
            TopTexture = "grass_block_top",
            SideTexture = "grass_block_side",
            BottomTexture = "dirt",
            Tint = "#9eff85",
            TintFaces = BlockTintFaces.Top,
        });

        // 2. 泥土
        Register(new BlockDef
        {
            Id = Dirt,
            Key = "cubit.voxel:dirt",
            Name = "泥土",
            TopTexture = "dirt",
            SideTexture = "dirt",
            BottomTexture = "dirt",
        });

        // 3. 石头
        Register(new BlockDef
        {
            Id = Stone,
            Key = "cubit.voxel:stone",
            Name = "石头",
            TopTexture = "stone",
            SideTexture = "stone",
            BottomTexture = "stone",
        });

        // 4. 沙子
        Register(new BlockDef
        {
            Id = Sand,
            Key = "cubit.voxel:sand",
            Name = "沙子",
            TopTexture = "sand",
            SideTexture = "sand",
            BottomTexture = "sand",
        });

        // 5. 通用木质方块：具体树种由项目内容包定义。
        Register(new BlockDef
        {
            Id = Wood,
            Key = "cubit.voxel:wood",
            Name = "木质方块",
            TopTexture = "dirt",
            SideTexture = "dirt",
            BottomTexture = "dirt",
        });

        // 6. 通用叶片方块：具体树种由项目内容包定义。
        Register(new BlockDef
        {
            Id = Leaves,
            Key = "cubit.voxel:leaves",
            Name = "叶片",
            TopTexture = "grass_block_top",
            SideTexture = "grass_block_top",
            BottomTexture = "grass_block_top",
        });

        // 7. 基岩
        Register(new BlockDef
        {
            Id = Bedrock,
            Key = "cubit.voxel:bedrock",
            Name = "基岩",
            TopTexture = "stone",
            SideTexture = "stone",
            BottomTexture = "stone",
        });
    }

    /// <summary>注册具有稳定 Key 的方块；兼容旧数字 ID，但不允许不一致定义静默覆盖。</summary>
    public static void Register(BlockDef def)
    {
        ArgumentNullException.ThrowIfNull(def);
        var key = NormalizeKey(def.Key, def.Id);
        var normalized = Clone(def, def.Id, key);
        ValidateDefinition(normalized);
        if (Blocks.Count == 0 && def.Id == Air)
        {
            // 首个注册（空气）：直接放入，避免自引用
            Blocks.Add(normalized);
            IdsByKey.Add(key, Air);
            _nextAutoId = 1;
            return;
        }

        if (IdsByKey.TryGetValue(key, out var existingId))
        {
            if (existingId != def.Id || !Equivalent(Blocks[existingId], normalized, key))
            {
                throw new InvalidDataException($"方块稳定 ID 重复且定义不一致: {key}");
            }

            return;
        }

        while (Blocks.Count <= normalized.Id)
        {
            Blocks.Add(Blocks[Air]);
        }

        var existing = Blocks[normalized.Id];
        if (existing.Id == normalized.Id && existing.Id != Air &&
            !string.IsNullOrWhiteSpace(existing.Key) && !string.Equals(existing.Key, key, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"方块运行时 ID 冲突: {normalized.Id} ({existing.Key} / {key})");
        }

        Blocks[normalized.Id] = normalized;
        IdsByKey[key] = normalized.Id;
        if (normalized.Id >= _nextAutoId)
        {
            _nextAutoId = normalized.Id + 1;
        }
    }

    /// <summary>注册方块并自动分配下一个可用 ID（内容包未指定 ID 时用）。</summary>
    public static ushort RegisterAuto(BlockDef def)
    {
        ArgumentNullException.ThrowIfNull(def);
        var key = NormalizeKey(def.Key, (ushort)_nextAutoId);
        if (IdsByKey.ContainsKey(key))
        {
            throw new InvalidDataException($"方块稳定 ID 重复: {key}");
        }

        if (_nextAutoId > ushort.MaxValue)
        {
            throw new InvalidOperationException("方块运行时 ID 已耗尽");
        }

        var id = (ushort)_nextAutoId;
        while (Blocks.Count <= id)
        {
            Blocks.Add(Blocks[Air]);
        }

        var clone = Clone(def, id, key);
        ValidateDefinition(clone);
        Blocks[id] = clone;
        IdsByKey[key] = id;
        _nextAutoId++;
        return id;
    }

    /// <summary>加载临时或工具内容包；有 Key 的方块在当前会话自动编号，不能用于项目持久存档。</summary>
    public static IReadOnlyList<string> LoadContentPack(string directory) =>
        LoadContentPackCore(directory, explicitIds: null);

    /// <summary>
    /// 按调用项目提供的 Key 到 ID 映射加载内容包。显式映射是项目持久 Section 的唯一编号来源。
    /// 插件不解释项目方块名、资源路径或玩法，仅校验映射与定义一致。
    /// </summary>
    public static IReadOnlyList<string> LoadContentPack(
        string directory,
        IReadOnlyDictionary<string, ushort> explicitIds)
    {
        ArgumentNullException.ThrowIfNull(explicitIds);
        return LoadContentPackCore(directory, NormalizeExplicitIds(explicitIds));
    }

    private static IReadOnlyList<string> LoadContentPackCore(
        string directory,
        IReadOnlyDictionary<string, ushort>? explicitIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        directory = Path.GetFullPath(directory);
        var blockDir = Path.Combine(directory, "blocks");
        var loaded = new List<string>();
        if (!Directory.Exists(blockDir))
        {
            return loaded;
        }

        var idMapSignature = ComputeExplicitIdMapSignature(explicitIds);
        // 同一项目运行会话可能因场景重载多次配置；内容包不是热重载入口，保持幂等。
        // 相同路径不得先用自动会话编号、再改为另一张持久编号表继续运行。
        if (LoadedContentPacks.Contains(directory))
        {
            if (!LoadedContentPackIdMapSignatures.TryGetValue(directory, out var loadedSignature) ||
                !string.Equals(loadedSignature, idMapSignature, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"内容包已用不同方块编号表加载: {directory}");
            }

            return loaded;
        }

        LoadedContentPacks.Add(directory);

        try
        {
            var files = Directory.EnumerateFiles(blockDir, "*.json", SearchOption.AllDirectories)
                .OrderBy(path => Path.GetRelativePath(blockDir, path).Replace('\\', '/'), StringComparer.Ordinal)
                .ToArray();
            var packHash = ComputePackHash(blockDir, files);
            var mappedKeys = explicitIds is null ? null : new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var def = Resource.FromJson<BlockDef>(File.ReadAllText(file));
                if (string.IsNullOrWhiteSpace(def.Key))
                {
                    if (explicitIds is not null)
                    {
                        throw new InvalidDataException($"持久内容包中的方块必须声明稳定 Key: {file}");
                    }

                    // 兼容旧内容包：显式数字 ID 仍可读取，但新内容应使用 Key。
                    var fallbackKey = $"legacy:{Slug(Path.GetFileNameWithoutExtension(file))}";
                    Register(Clone(def, def.Id, fallbackKey));
                    loaded.Add(fallbackKey);
                }
                else
                {
                    var key = NormalizeKey(def.Key, 0);
                    if (explicitIds is not null)
                    {
                        if (!explicitIds.TryGetValue(key, out var assignedId))
                        {
                            throw new InvalidDataException($"持久内容包方块缺少项目编号: {key}");
                        }

                        var normalized = Clone(def, assignedId, key);
                        if (IdsByKey.TryGetValue(key, out var existingId))
                        {
                            if (existingId != assignedId || !Equivalent(Blocks[existingId], normalized, key))
                            {
                                throw new InvalidDataException($"持久内容包方块编号或定义不一致: {key}");
                            }
                        }
                        else
                        {
                            Register(normalized);
                        }

                        mappedKeys!.Add(key);
                        loaded.Add(key);
                        continue;
                    }

                    if (IdsByKey.TryGetValue(key, out var autoExistingId))
                    {
                        if (!Equivalent(Blocks[autoExistingId], def, key))
                        {
                            throw new InvalidDataException($"方块稳定 ID 重复且定义不一致: {key}");
                        }
                    }
                    else
                    {
                        _ = RegisterAuto(def);
                    }

                    loaded.Add(key);
                }
            }

            if (explicitIds is not null)
            {
                var staleKeys = explicitIds.Keys
                    .Where(key => !mappedKeys!.Contains(key))
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray();
                if (staleKeys.Length > 0)
                {
                    throw new InvalidDataException($"项目方块编号表包含不存在的内容定义: {string.Join(", ", staleKeys)}");
                }
            }

            LoadedContentPackHashes[directory] = packHash;
            LoadedContentPackIdMapSignatures[directory] = idMapSignature;
        }
        catch
        {
            LoadedContentPacks.Remove(directory);
            LoadedContentPackHashes.Remove(directory);
            LoadedContentPackIdMapSignatures.Remove(directory);
            throw;
        }

        return loaded;
    }

    private static IReadOnlyDictionary<string, ushort> NormalizeExplicitIds(
        IReadOnlyDictionary<string, ushort> explicitIds)
    {
        if (explicitIds.Count == 0)
        {
            throw new InvalidDataException("持久内容包方块编号表不能为空");
        }

        var normalized = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var usedIds = new HashSet<ushort>();
        foreach (var (rawKey, id) in explicitIds)
        {
            var key = NormalizeKey(rawKey, 0);
            if (id <= Bedrock)
            {
                throw new InvalidDataException($"持久内容包方块编号不可占用内置区间 0..{Bedrock}: {key}={id}");
            }

            if (!normalized.TryAdd(key, id))
            {
                throw new InvalidDataException($"持久内容包方块 Key 重复: {key}");
            }

            if (!usedIds.Add(id))
            {
                throw new InvalidDataException($"持久内容包方块编号重复: {id}");
            }
        }

        return normalized;
    }

    private static string ComputeExplicitIdMapSignature(IReadOnlyDictionary<string, ushort>? explicitIds)
    {
        if (explicitIds is null)
        {
            return "session-auto";
        }

        var text = string.Join(
            '\n',
            explicitIds.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    public static int Count => Blocks.Count;

    public static BlockDef Get(ushort block)
    {
        if (block >= Blocks.Count)
        {
            throw new IndexOutOfRangeException($"方块 ID {block} 未注册（当前共 {Blocks.Count} 个）");
        }

        return Blocks[block];
    }

    public static string GetName(ushort block) => Get(block).Name;

    /// <summary>按稳定 namespace:block_name 解析当前会话数字 ID。</summary>
    public static ushort GetId(string key) =>
        !string.IsNullOrWhiteSpace(key) && IdsByKey.TryGetValue(NormalizeKey(key, 0), out var id)
            ? id
            : throw new KeyNotFoundException($"方块稳定 ID 未注册: {key}");

    /// <summary>返回方块稳定内容 ID；未知数字 ID 直接拒绝。</summary>
    public static string GetKey(ushort block) => Get(block).Key;

    public static bool IsRegistered(ushort block) => block < Blocks.Count &&
        Blocks[block] is { } def && def.Id == block && !string.IsNullOrWhiteSpace(def.Key);

    /// <summary>方块是否提供可提交的渲染几何；空气始终不渲染。</summary>
    public static bool IsRenderable(ushort block) =>
        block != Air && Get(block).RenderShape is BlockRenderShape.Cube or BlockRenderShape.Cross or BlockRenderShape.Fluid;

    public static bool IsSolid(ushort block) => Get(block).Solid;

    public static bool IsOpaque(ushort block) => Get(block).Opaque;

    /// <summary>方块是否使用插件的流体状态和网格路径。</summary>
    public static bool IsFluid(ushort block) => Get(block).RenderShape == BlockRenderShape.Fluid;

    /// <summary>返回方块的光线衰减，调用方据此决定传播而不解释具体内容。</summary>
    public static int GetLightAttenuation(ushort block) => Get(block).LightAttenuation;

    /// <summary>由 ID 构造默认状态（Data=0）。</summary>
    public static BlockState GetState(ushort block) => new(block);

    /// <summary>按数字 ID 或名称（含部分匹配，忽略大小写）查找方块。</summary>
    public static ushort? FindId(string nameOrId)
    {
        if (ushort.TryParse(nameOrId, out var parsed) && parsed < Blocks.Count)
        {
            return parsed;
        }

        if (!string.IsNullOrWhiteSpace(nameOrId) && IdsByKey.TryGetValue(nameOrId.Trim(), out var keyed))
        {
            return keyed;
        }

        for (var i = 0; i < Blocks.Count; i++)
        {
            var def = Blocks[i];
            if (def is not null && (def.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase)
                || def.Name.Contains(nameOrId, StringComparison.OrdinalIgnoreCase)))
            {
                return (ushort)i;
            }
        }

        return null;
    }

    private static BlockDef Clone(BlockDef def, ushort id, string key) => new()
    {
        Id = id,
        Key = key,
        Name = def.Name,
        TopTexture = def.TopTexture,
        SideTexture = def.SideTexture,
        BottomTexture = def.BottomTexture,
        Tint = BlockDef.NormalizeTint(def.Tint),
        TintFaces = BlockDef.NormalizeTintFaces(def.TintFaces),
        RenderShape = NormalizeRenderShape(def.RenderShape),
        Solid = def.Solid,
        Opaque = def.Opaque,
        LightLevel = def.LightLevel,
        LightAttenuation = def.LightAttenuation,
    };

    private static bool Equivalent(BlockDef existing, BlockDef incoming, string key) =>
        string.Equals(existing.Key, key, StringComparison.Ordinal) &&
        string.Equals(existing.Name, incoming.Name, StringComparison.Ordinal) &&
        string.Equals(existing.TopTexture, incoming.TopTexture, StringComparison.Ordinal) &&
        string.Equals(existing.SideTexture, incoming.SideTexture, StringComparison.Ordinal) &&
        string.Equals(existing.BottomTexture, incoming.BottomTexture, StringComparison.Ordinal) &&
        string.Equals(existing.Tint, BlockDef.NormalizeTint(incoming.Tint), StringComparison.Ordinal) &&
        existing.TintFaces == BlockDef.NormalizeTintFaces(incoming.TintFaces) &&
        existing.RenderShape == NormalizeRenderShape(incoming.RenderShape) &&
        existing.Solid == incoming.Solid && existing.Opaque == incoming.Opaque &&
        existing.LightLevel == incoming.LightLevel &&
        existing.LightAttenuation == incoming.LightAttenuation;

    private static void ValidateDefinition(BlockDef def)
    {
        if (def.LightAttenuation is < 0 or > 15)
        {
            throw new InvalidDataException($"方块透光衰减必须在 0..15: {def.Key}");
        }

        if (def.RenderShape != BlockRenderShape.Fluid)
        {
            return;
        }

        if (def.Solid || def.Opaque)
        {
            throw new InvalidDataException($"流体方块必须非实体且不遮挡: {def.Key}");
        }

        if (def.LightAttenuation == 0)
        {
            throw new InvalidDataException($"流体方块必须定义透光衰减: {def.Key}");
        }
    }

    private static BlockRenderShape NormalizeRenderShape(BlockRenderShape renderShape) =>
        Enum.IsDefined(renderShape)
            ? renderShape
            : throw new InvalidDataException($"未知方块渲染形状: {renderShape}");

    private static string NormalizeKey(string? key, ushort fallbackId)
    {
        var value = string.IsNullOrWhiteSpace(key) ? $"cubit.legacy:id_{fallbackId}" : key.Trim();
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1 || value.IndexOf(':', separator + 1) >= 0)
        {
            throw new InvalidDataException($"方块稳定 ID 必须是 namespace:block_name: {value}");
        }

        var ns = value[..separator];
        var name = value[(separator + 1)..];
        if (!IsValidKeyPart(ns) || !IsValidKeyPart(name) || !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"方块稳定 ID 格式无效: {value}");
        }

        return value;
    }

    private static bool IsValidKeyPart(string value) => value.All(ch =>
        ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.');

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(ch => IsValidKeyPart(ch.ToString()) ? ch : '_').ToArray();
        return new string(chars).Trim('_') is { Length: > 0 } slug ? slug : "block";
    }

    private static string ComputeContentHash()
    {
        var builder = new StringBuilder();
        foreach (var def in Blocks.Where(def => def is not null).OrderBy(def => def.Key, StringComparer.Ordinal))
        {
            builder.Append(def.Key).Append('|').Append(def.Id).Append('|').Append(def.Name).Append('|')
                .Append(def.TopTexture).Append('|').Append(def.SideTexture).Append('|').Append(def.BottomTexture).Append('|')
                .Append(def.Solid).Append('|').Append(def.Opaque).Append('|').Append(def.LightLevel).Append('|')
                .Append(def.LightAttenuation).Append('\n');
            if (def.TintFaces != BlockTintFaces.All)
            {
                builder.Append("tintFaces|").Append(def.Key).Append('|').Append(def.TintFaces).Append('\n');
            }
            if (def.RenderShape != BlockRenderShape.Cube)
            {
                // 默认 Cube 不写入历史摘要，保持旧内容的摘要稳定；非默认形状必须参与兼容判断。
                builder.Append("renderShape|").Append(def.Key).Append('|').Append(def.RenderShape).Append('\n');
            }
        }

        // 存档兼容性只描述内容，不描述本机绝对路径；同一包被临时复制或从不同入口加载时必须稳定。
        foreach (var packHash in LoadedContentPackHashes.Values
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(hash => hash, StringComparer.Ordinal))
        {
            builder.Append("pack|").Append(packHash).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string ComputePackHash(string blockDirectory, IReadOnlyList<string> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(blockDirectory, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(file));
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
