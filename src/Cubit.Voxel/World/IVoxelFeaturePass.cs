namespace Cubit.Voxel.World;

/// <summary>项目/插件可注入的体素特征生成契约；提交和区块存储仍由 Voxel 主线程流水线负责。</summary>
public interface IVoxelFeaturePass
{
    string Id { get; }

    FeaturePlacementBuffer Generate(
        in ChunkGenerationContext context,
        int originX,
        int originY,
        int originZ,
        uint seed);
}
