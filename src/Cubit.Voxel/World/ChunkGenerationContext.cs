namespace Cubit.Voxel.World;

/// <summary>区块生成阶段的只读上下文；不持有 ChunkStore，也不允许 worker 直接提交世界状态。</summary>
public readonly record struct ChunkGenerationContext(
    int ChunkX,
    int ChunkZ,
    uint Seed,
    Func<int, int, int, bool>? CanPlace = null,
    WorldType WorldType = WorldType.Noise,
    Func<int, int, int>? SurfaceYQuery = null,
    Func<int, int, int, BlockState>? BlockAt = null)
{
    public bool IsPlaceable(int x, int y, int z) => CanPlace?.Invoke(x, y, z) ?? true;

    /// <summary>返回指定列中地表上方第一个可放置 Y；特征只能读取，不能改写生成中的区块。</summary>
    public int GetSurfaceY(int x, int z) => (SurfaceYQuery
        ?? throw new InvalidOperationException("当前体素特征上下文未提供地表高度查询"))(x, z);

    /// <summary>读取当前特征上下文允许观察的方块状态；用于通用特征的落地条件判断。</summary>
    public BlockState GetBlock(int x, int y, int z) => (BlockAt
        ?? throw new InvalidOperationException("当前体素特征上下文未提供方块查询"))(x, y, z);
}
