namespace Cubit.Voxel.World;

/// <summary>通用树干放置器；只生成待提交写入，不访问区块存储。</summary>
public abstract class TrunkPlacer
{
    protected TrunkPlacer(int baseHeight, int heightRandA, int heightRandB)
    {
        if (baseHeight <= 0 || heightRandA < 0 || heightRandB < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseHeight), "树干高度参数无效");
        }

        BaseHeight = baseHeight;
        HeightRandA = heightRandA;
        HeightRandB = heightRandB;
    }

    public int BaseHeight { get; }

    public int HeightRandA { get; }

    public int HeightRandB { get; }

    public abstract void Place(
        TreeFeatureRecipe recipe,
        in ChunkGenerationContext context,
        FeaturePlacementBuffer.Builder builder,
        int x,
        int y,
        int z,
        uint seed);

    protected int ResolveHeight(uint seed)
    {
        var first = Mix(seed ^ 0x9E3779B9u) % (uint)(HeightRandA + 1);
        var second = Mix(seed ^ 0x85EBCA6Bu) % (uint)(HeightRandB + 1);
        return checked(BaseHeight + (int)first + (int)second);
    }

    protected static uint Mix(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        return value ^ (value >> 16);
    }
}

/// <summary>原版直树干策略的通用最小实现。</summary>
public sealed class StraightTrunkPlacer(int baseHeight, int heightRandA, int heightRandB) :
    TrunkPlacer(baseHeight, heightRandA, heightRandB)
{
    public override void Place(
        TreeFeatureRecipe recipe,
        in ChunkGenerationContext context,
        FeaturePlacementBuffer.Builder builder,
        int x,
        int y,
        int z,
        uint seed)
    {
        var height = ResolveHeight(seed);
        for (var offset = 0; offset < height; offset++)
        {
            builder.Add(x, y + offset, z, recipe.TrunkBlockKey);
        }

        // 对齐原版 StraightTrunkPlacer：树叶附着点是 origin.above(treeHeight)，即最后一段树干上方一格。
        recipe.FoliagePlacer.Place(recipe, in context, builder, x, y + height, z, seed);
    }
}
