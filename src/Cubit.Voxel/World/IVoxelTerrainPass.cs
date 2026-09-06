namespace Cubit.Voxel.World;

/// <summary>项目或官方插件注入的生成前地形列通道；只能基于绝对坐标和不可变上下文返回列结果。</summary>
public interface IVoxelTerrainPass
{
    /// <summary>稳定通道标识；世界按此顺序应用通道，保证生成顺序无关。</summary>
    string Id { get; }

    /// <summary>在基础列上计算新的地表和可选静态流体意图。</summary>
    VoxelTerrainColumn Evaluate(
        in VoxelTerrainColumnContext context,
        in VoxelTerrainColumn column);
}
