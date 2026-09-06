using System.Reflection;
using StbImageSharp;
using Cubit.Voxel.World;

namespace Cubit.Voxel.Textures;

/// <summary>
/// 方块纹理图集：把方块注册表引用的纹理打包成一张图集。
/// 本地原版纹理为灰度占位时，按纹理定义乘以上色（老 MC 的 tint 思路）。
/// 纹理来自本地 Minecraft 实例资源，仅用于学习用途。
/// </summary>
public static class TextureAtlas
{
    public const int TileSize = 16;
    public const int Columns = 8;

    // 内置纹理名 -> (文件名, RGB 色调)。内容包可用同一机制追加不同色调版本。
    private static readonly (string Name, string File, uint TintRgb)[] Textures =
    [
        ("grass_block_top", "grass_block_top.png", 0x9EFF85u),
        ("grass_block_side", "grass_block_side.png", 0xFFFFFFu),
        ("dirt", "dirt.png", 0xFFFFFFu),
        ("stone", "stone.png", 0xFFFFFFu),
        ("sand", "sand.png", 0xFFFFFFu),
    ];

    private static Dictionary<TextureTileKey, int> TileIndex = [];
    private static byte[]? _atlasData;
    private static int _atlasWidth;
    private static int _atlasHeight;
    private static string? _contentHash;
    private static string? _projectTextureDirectory;
    private static int _tileCount;

    /// <summary>由项目显式提供纹理目录；插件不会扫描项目资产。</summary>
    public static void ConfigureProjectTextureDirectory(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var fullPath = Path.GetFullPath(directory);
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException($"项目方块纹理目录不存在: {fullPath}");
            }

            _projectTextureDirectory = fullPath;
        }
        else
        {
            _projectTextureDirectory = null;
        }

        Invalidate();
    }

    public static byte[] AtlasData
    {
        get
        {
            EnsureCurrent();
            return _atlasData!;
        }
    }

    public static int AtlasWidth
    {
        get
        {
            EnsureCurrent();
            return _atlasWidth;
        }
    }

    public static int AtlasHeight
    {
        get
        {
            EnsureCurrent();
            return _atlasHeight;
        }
    }

    /// <summary>某方块某面的瓦片索引（face: 0顶 1底 2/3侧Z 4/5侧X）。</summary>
    public static int GetTile(ushort block, int face)
    {
        EnsureCurrent();
        var def = BlockRegistry.Get(block);
        var key = new TextureTileKey(GetTextureName(def, face), GetFaceTint(def, face));
        return TileIndex.TryGetValue(key, out var tile)
            ? tile
            : TileIndex[new TextureTileKey("stone", 0xFFFFFFu)];
    }

    public static (float U0, float V0, float U1, float V1) GetUv(int tile)
    {
        EnsureCurrent();
        var row = tile / Columns;
        var col = tile % Columns;
        var rows = (_tileCount + Columns - 1) / Columns;
        var u0 = col / (float)Columns;
        var v0 = row / (float)rows;
        var u1 = (col + 1) / (float)Columns;
        var v1 = (row + 1) / (float)rows;
        return (u0, v0, u1, v1);
    }

    private static Dictionary<TextureTileKey, int> BuildTileIndex(IReadOnlyList<TextureEntry> textures)
    {
        var map = new Dictionary<TextureTileKey, int>();
        for (var i = 0; i < textures.Count; i++)
        {
            map[new TextureTileKey(textures[i].Name, textures[i].TintRgb)] = i;
        }

        return map;
    }

    private static byte[] BuildAtlas(out int width, out int height)
    {
        var textures = BuildTextureList();
        var rows = (textures.Count + Columns - 1) / Columns;
        width = Columns * TileSize;
        height = rows * TileSize;
        var atlas = new byte[width * height * 4];

        for (var i = 0; i < textures.Count; i++)
        {
            var texture = textures[i];
            var tile = LoadTintedTile(texture);
            var row = i / Columns;
            var col = i % Columns;
            var offsetX = col * TileSize;
            var offsetY = row * TileSize;
            for (var y = 0; y < TileSize; y++)
            {
                var srcStart = y * TileSize * 4;
                var dstStart = ((offsetY + y) * width + offsetX) * 4;
                Array.Copy(tile, srcStart, atlas, dstStart, TileSize * 4);
            }
        }

        return atlas;
    }

    private static byte[] LoadTintedTile(TextureEntry texture)
    {
        Stream stream;
        var external = _projectTextureDirectory is null
            ? null
            : Path.Combine(_projectTextureDirectory, texture.File);
        if (external is not null && File.Exists(external))
        {
            stream = File.OpenRead(external);
        }
        else
        {
            var resourceName = $"Cubit.Voxel.Textures.{texture.File}";
            stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException($"缺少项目或插件纹理资源: {texture.Name}");
        }

        using (stream)
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            return BuildTintedTile(ms.ToArray(), texture);
        }
    }

    private static byte[] BuildTintedTile(byte[] encoded, TextureEntry texture)
    {
        var image = ImageResult.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha);
        var data = image.Width != TileSize || image.Height != TileSize
            ? ResizeNearest(image, TileSize, TileSize)
            : image.Data;

        if (texture.TintRgb == 0xFFFFFFu)
        {
            return data;
        }

        var red = (byte)(texture.TintRgb >> 16);
        var green = (byte)(texture.TintRgb >> 8);
        var blue = (byte)texture.TintRgb;
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = (byte)(data[i] * red / 255);
            data[i + 1] = (byte)(data[i + 1] * green / 255);
            data[i + 2] = (byte)(data[i + 2] * blue / 255);
        }

        return data;
    }

    private static byte[] ResizeNearest(ImageResult image, int newW, int newH)
    {
        var result = new byte[newW * newH * 4];
        for (var y = 0; y < newH; y++)
        {
            var sy = Math.Clamp(y * image.Height / newH, 0, image.Height - 1);
            for (var x = 0; x < newW; x++)
            {
                var sx = Math.Clamp(x * image.Width / newW, 0, image.Width - 1);
                var src = (sy * image.Width + sx) * 4;
                var dst = (y * newW + x) * 4;
                result[dst] = image.Data[src];
                result[dst + 1] = image.Data[src + 1];
                result[dst + 2] = image.Data[src + 2];
                result[dst + 3] = image.Data[src + 3];
            }
        }

        return result;
    }

    private static void EnsureCurrent()
    {
        var hash = BlockRegistry.ContentHash + "|" + _projectTextureDirectory;
        if (_atlasData is not null && string.Equals(hash, _contentHash, StringComparison.Ordinal))
        {
            return;
        }

        _atlasData = BuildAtlas(out _atlasWidth, out _atlasHeight);
        var textures = BuildTextureList();
        TileIndex = BuildTileIndex(textures);
        _tileCount = textures.Count;
        _contentHash = hash;
    }

    private static void Invalidate()
    {
        _atlasData = null;
        _contentHash = null;
    }

    private static List<TextureEntry> BuildTextureList()
    {
        var entries = Textures.Select(texture => new TextureEntry(texture.Name, texture.File, texture.TintRgb))
            .ToList();
        var keys = entries.Select(entry => new TextureTileKey(entry.Name, entry.TintRgb)).ToHashSet();
        foreach (var def in BlockRegistry.Definitions.OrderBy(definition => definition.Key, StringComparer.Ordinal))
        {
            for (var face = 0; face < 6; face++)
            {
                var name = GetTextureName(def, face);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var key = new TextureTileKey(name, GetFaceTint(def, face));
                if (keys.Add(key))
                {
                    entries.Add(new TextureEntry(
                        name,
                        name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name : name + ".png",
                        key.TintRgb));
                }
            }
        }

        return entries;
    }

    private static string GetTextureName(BlockDef def, int face) => face switch
    {
        0 => def.TopTexture,
        1 => def.BottomTexture.Length > 0 ? def.BottomTexture : def.SideTexture,
        _ => def.SideTexture,
    };

    private static uint GetFaceTint(BlockDef def, int face)
    {
        var faceFlag = (BlockTintFaces)(1 << face);
        return (BlockDef.NormalizeTintFaces(def.TintFaces) & faceFlag) != 0
            ? BlockDef.GetTintRgb(def.Tint)
            : 0xFFFFFFu;
    }

    private readonly record struct TextureTileKey(string Name, uint TintRgb);

    private sealed record TextureEntry(string Name, string File, uint TintRgb);
}
