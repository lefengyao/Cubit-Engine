namespace Cubit.Voxel.World;

/// <summary>
/// 方块实体（借鉴 MC TileEntity/BlockEntity）：挂在某个方块位置上的动态行为对象，
/// 由 VoxelWorld.Tick 每模拟 tick 驱动一次，可承载生长/存储/红石等状态。
/// </summary>
public abstract class BlockEntity
{
    public VoxelWorld? World { get; internal set; }

    public int X { get; internal set; }

    public int Y { get; internal set; }

    public int Z { get; internal set; }

    /// <summary>每模拟 tick 调用一次（世界持有并驱动）。</summary>
    public abstract void Tick();
}

/// <summary>方块实体工厂注册表：方块 ID → 实体工厂（内容方注册，引擎提供机制）。</summary>
public static class BlockEntityRegistry
{
    private static readonly Dictionary<ushort, Func<BlockEntity>> Factories = [];

    public static void Register(ushort blockId, Func<BlockEntity> factory)
    {
        Factories[blockId] = factory;
    }

    public static bool Has(ushort blockId) => Factories.ContainsKey(blockId);

    public static BlockEntity? Create(ushort blockId) =>
        Factories.TryGetValue(blockId, out var factory) ? factory() : null;
}