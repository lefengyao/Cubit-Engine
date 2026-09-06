namespace Cubit.Voxel.World;

/// <summary>体素特征放置入口；生成阶段只返回不可变 buffer。</summary>
public static class TreeFeaturePlacement
{
    public static FeaturePlacementBuffer Place(
        TreeFeatureRecipe recipe,
        in ChunkGenerationContext context,
        int x,
        int y,
        int z,
        uint seed) =>
        (recipe ?? throw new ArgumentNullException(nameof(recipe))).Place(in context, x, y, z, seed);
}
