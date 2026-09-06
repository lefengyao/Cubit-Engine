namespace Cubit.Voxel.World;

/// <summary>通用树叶放置器；只向不可变写入缓冲追加候选方块。</summary>
public abstract class FoliagePlacer
{
    protected FoliagePlacer(int radius, int offset, int height)
    {
        if (radius < 0 || offset < 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "树叶放置参数无效");
        }

        Radius = radius;
        Offset = offset;
        Height = height;
    }

    public int Radius { get; }

    public int Offset { get; }

    public int Height { get; }

    public abstract void Place(
        TreeFeatureRecipe recipe,
        in ChunkGenerationContext context,
        FeaturePlacementBuffer.Builder builder,
        int x,
        int topY,
        int z,
        uint seed);
}

/// <summary>原版 blob foliage 的确定性圆形层实现。</summary>
public sealed class BlobFoliagePlacer(int radius, int offset, int height) : FoliagePlacer(radius, offset, height)
{
    public override void Place(
        TreeFeatureRecipe recipe,
        in ChunkGenerationContext context,
        FeaturePlacementBuffer.Builder builder,
        int x,
        int topY,
        int z,
        uint seed)
    {
        // 对齐原版 BlobFoliagePlacer：yo 从 offset 递减到 offset - foliageHeight，包含两端。
        for (var yo = Offset; yo >= Offset - Height; yo--)
        {
            var currentRadius = Math.Max(Radius - 1 - yo / 2, 0);
            for (var dx = -currentRadius; dx <= currentRadius; dx++)
            {
                for (var dz = -currentRadius; dz <= currentRadius; dz++)
                {
                    // 原版仅随机跳过每层的四个角；同种子/坐标在 Cubit 中保持稳定。
                    if (Math.Abs(dx) == currentRadius && Math.Abs(dz) == currentRadius &&
                        (yo == 0 || (Mix(seed, x + dx, topY + yo, z + dz) & 1u) == 0u))
                    {
                        continue;
                    }

                    builder.Add(x + dx, topY + yo, z + dz, recipe.FoliageBlockKey);
                }
            }
        }
    }

    private static uint Mix(uint seed, int x, int y, int z)
    {
        var value = seed ^ unchecked((uint)(x * 73428767)) ^
            unchecked((uint)(y * 912931)) ^ unchecked((uint)(z * 19349663));
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        return value ^ (value >> 16);
    }
}
