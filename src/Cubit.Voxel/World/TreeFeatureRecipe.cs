namespace Cubit.Voxel.World;

/// <summary>树特征的通用稳定配方；方块使用 Key，放置算法由可替换策略提供。</summary>
public sealed class TreeFeatureRecipe
{
    public TreeFeatureRecipe(
        string trunkBlockKey,
        string foliageBlockKey,
        TrunkPlacer trunkPlacer,
        FoliagePlacer foliagePlacer,
        int featureWidth,
        int featureHeight,
        int featureDepth)
    {
        TrunkBlockKey = ValidateBlockKey(trunkBlockKey, nameof(trunkBlockKey));
        FoliageBlockKey = ValidateBlockKey(foliageBlockKey, nameof(foliageBlockKey));
        TrunkPlacer = trunkPlacer ?? throw new ArgumentNullException(nameof(trunkPlacer));
        FoliagePlacer = foliagePlacer ?? throw new ArgumentNullException(nameof(foliagePlacer));
        if (featureWidth <= 0 || featureHeight <= 0 || featureDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(featureWidth), "树特征尺寸必须为正数");
        }

        FeatureWidth = featureWidth;
        FeatureHeight = featureHeight;
        FeatureDepth = featureDepth;
    }

    public string TrunkBlockKey { get; }

    public string FoliageBlockKey { get; }

    public TrunkPlacer TrunkPlacer { get; }

    public FoliagePlacer FoliagePlacer { get; }

    public int FeatureWidth { get; }

    public int FeatureHeight { get; }

    public int FeatureDepth { get; }

    public FeaturePlacementBuffer Place(
        in ChunkGenerationContext context,
        int x,
        int y,
        int z,
        uint seed)
    {
        var builder = FeaturePlacementBuffer.CreateBuilder(context);
        TrunkPlacer.Place(this, in context, builder, x, y, z, seed);
        return builder.Build();
    }

    private static string ValidateBlockKey(string key, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, parameterName);
        var separator = key.IndexOf(':');
        if (separator <= 0 || separator == key.Length - 1 || key.IndexOf(':', separator + 1) >= 0 ||
            !string.Equals(key, key.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"方块 Key 必须是稳定的小写 namespace:block_name: {key}", parameterName);
        }

        return key;
    }
}
