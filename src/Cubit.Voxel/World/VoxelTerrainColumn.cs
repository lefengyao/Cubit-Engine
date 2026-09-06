namespace Cubit.Voxel.World;

/// <summary>地形列通道的只读输入；不包含 ChunkStore 或邻区可变状态。</summary>
public readonly record struct VoxelTerrainColumnContext(
    int X,
    int Z,
    uint Seed,
    WorldType WorldType,
    int BaseSurfaceY);

/// <summary>生成前地形列的最终结果；静态流体只填充在地表之上的连续区间。</summary>
public readonly record struct VoxelTerrainColumn(
    int SurfaceY,
    int FluidSurfaceY,
    BlockState StaticFluid)
{
    /// <summary>不含流体的基础列。</summary>
    public static VoxelTerrainColumn CreateBase(int surfaceY) => new(surfaceY, surfaceY, BlockState.Air);

    /// <summary>列中最高已占用方块上方的第一个 Y，供出生点和特征放置查询。</summary>
    public int AboveSurfaceY => (HasStaticFluid ? FluidSurfaceY : SurfaceY) + 1;

    /// <summary>是否需要在地形表面上方填充静态流体。</summary>
    public bool HasStaticFluid => !StaticFluid.IsAir;

    /// <summary>验证垂直边界和 W1 静态流体状态，拒绝把无效列悄悄写入区块。</summary>
    public void Validate()
    {
        if (SurfaceY < Chunk.MinY + 1 || SurfaceY > Chunk.MaxYExclusive - 2)
        {
            throw new InvalidDataException($"地形列地表高度越出垂直边界: {SurfaceY}");
        }

        if (!HasStaticFluid)
        {
            if (StaticFluid.Data != 0 || FluidSurfaceY != SurfaceY)
            {
                throw new InvalidDataException("无静态流体的地形列必须使用空气状态和相同的流体表面高度");
            }

            return;
        }

        if (FluidSurfaceY <= SurfaceY || FluidSurfaceY > Chunk.MaxYExclusive - 2)
        {
            throw new InvalidDataException(
                $"静态流体表面必须在地表之上且保留顶部空气格: surface={SurfaceY} fluid={FluidSurfaceY}");
        }

        VoxelFluidState.ValidateStaticSource(StaticFluid);
    }
}
