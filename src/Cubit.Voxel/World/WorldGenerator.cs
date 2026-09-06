namespace Cubit.Voxel.World;

/// <summary>
/// 世界生成器：确定性值噪声（fBm）生成高度图地形。
/// 同一种子永远生成相同世界，便于测试。
/// </summary>
public static class WorldGenerator
{
    private const int SeaLevel = 18;

    public static int GetHeight(int x, int z, uint seed)
    {
        var noise = 0f;
        var amplitude = 1f;
        var frequency = 0.012f;
        for (var octave = 0; octave < 4; octave++)
        {
            noise += ValueNoise2D(x * frequency, z * frequency, seed + (uint)octave * 101) * amplitude;
            amplitude *= 0.5f;
            frequency *= 2f;
        }

        // 值噪声范围约 [0,1]，累加后除以总振幅得到 [0,1]
        noise /= (1f + 0.5f + 0.25f + 0.125f);
        var height = 24f + noise * 22f;
        return (int)MathF.Floor(height);
    }

    /// <summary>计算噪声或平坦世界的最终地形列；通道只读取绝对坐标的不可变输入。</summary>
    public static VoxelTerrainColumn GetTerrainColumn(
        int x,
        int z,
        uint seed,
        WorldType worldType,
        IReadOnlyList<IVoxelTerrainPass>? terrainPasses = null)
    {
        var baseSurfaceY = worldType switch
        {
            WorldType.Flat => FlatLayers.Length - 1,
            _ => Math.Clamp(GetHeight(x, z, seed), Chunk.MinY + 1, Chunk.MaxYExclusive - 2),
        };
        var column = VoxelTerrainColumn.CreateBase(baseSurfaceY);

        // Flat 保持既有固定层叠定义，不执行项目地形通道。
        if (worldType == WorldType.Flat || terrainPasses is null)
        {
            return column;
        }

        var context = new VoxelTerrainColumnContext(x, z, seed, worldType, baseSurfaceY);
        foreach (var pass in terrainPasses)
        {
            ArgumentNullException.ThrowIfNull(pass);
            column = pass.Evaluate(in context, in column);
        }

        column.Validate();
        return column;
    }

    /// <summary>从已经验证的最终列推导指定 Y 的基础生成方块，不读取区块存储。</summary>
    public static BlockState GetColumnBlock(
        in VoxelTerrainColumn column,
        int y,
        SurfaceRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!Chunk.IsValidY(y))
        {
            return BlockState.Air;
        }

        if (y <= column.SurfaceY)
        {
            return rules.Resolve(y, column.SurfaceY, SeaLevel);
        }

        return column.HasStaticFluid && y <= column.FluidSurfaceY
            ? column.StaticFluid
            : BlockState.Air;
    }

    /// <summary>超平坦层叠（原版思路：固定层叠方块，无噪声）。经典：基岩/石头/石头/泥土/草。</summary>
    public static readonly ushort[] FlatLayers =
    [
        BlockRegistry.Bedrock,
        BlockRegistry.Stone,
        BlockRegistry.Stone,
        BlockRegistry.Dirt,
        BlockRegistry.Grass,
    ];

    /// <summary>生成超平坦区块：每一列都是同样的层叠方块。</summary>
    public static void GenerateFlatChunk(Chunk chunk) => GenerateFlatChunk(chunk, CancellationToken.None);

    public static void GenerateFlatChunk(Chunk chunk, CancellationToken cancellationToken)
    {
        for (var layer = 0; layer < FlatLayers.Length && layer < Chunk.MaxYExclusive; layer++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var y = layer;
            var block = FlatLayers[layer];
            for (var x = 0; x < Chunk.SizeX; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var z = 0; z < Chunk.SizeZ; z++)
                {
                    chunk.SetBlock(x, y, z, block);
                }
            }
        }
    }
    public static void GenerateChunk(Chunk chunk, int chunkX, int chunkZ, uint seed, SurfaceRules rules) =>
        GenerateChunk(chunk, chunkX, chunkZ, seed, rules, terrainPasses: null, CancellationToken.None);

    public static void GenerateChunk(
        Chunk chunk,
        int chunkX,
        int chunkZ,
        uint seed,
        SurfaceRules rules,
        IReadOnlyList<IVoxelTerrainPass>? terrainPasses,
        CancellationToken cancellationToken,
        IDictionary<(int X, int Z), VoxelTerrainColumn>? terrainCache = null)
    {
        for (var x = 0; x < Chunk.SizeX; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var z = 0; z < Chunk.SizeZ; z++)
            {
                var wx = chunkX * Chunk.SizeX + x;
                var wz = chunkZ * Chunk.SizeZ + z;
                var key = (wx, wz);
                if (terrainCache is null || !terrainCache.TryGetValue(key, out var column))
                {
                    column = GetTerrainColumn(wx, wz, seed, WorldType.Noise, terrainPasses);
                    terrainCache?.Add(key, column);
                }

                // 噪声世界覆盖插件当前垂直契约；Flat 世界仍由独立的 FlatLayers 保持项目兼容布局。
                for (var y = Chunk.MinY; y <= column.FluidSurfaceY; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var block = GetColumnBlock(in column, y, rules);
                    if (!block.IsAir)
                    {
                        chunk.SetBlock(x, y, z, block);
                    }
                }
            }
        }

    }

    /// <summary>确定性值噪声：哈希 + 双线性平滑插值，输出 [0,1]。</summary>
    private static float ValueNoise2D(float x, float z, uint seed)
    {
        var x0 = (int)MathF.Floor(x);
        var z0 = (int)MathF.Floor(z);
        var fx = x - x0;
        var fz = z - z0;

        var v00 = Hash(x0, z0, seed);
        var v10 = Hash(x0 + 1, z0, seed);
        var v01 = Hash(x0, z0 + 1, seed);
        var v11 = Hash(x0 + 1, z0 + 1, seed);

        var sx = SmoothStep(fx);
        var sz = SmoothStep(fz);

        var top = v00 + (v10 - v00) * sx;
        var bottom = v01 + (v11 - v01) * sx;
        return top + (bottom - top) * sz;
    }

    private static float Hash(int x, int z, uint seed)
    {
        var h = (uint)(x * 374761393 + z * 668265263) ^ seed;
        h = (h ^ (h >> 13)) * 1274126177;
        h ^= h >> 16;
        return (h & 0xFFFF) / 65535f;
    }

    private static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

