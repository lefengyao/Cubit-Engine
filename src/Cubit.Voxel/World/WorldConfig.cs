using Cubit.Core.Scene;
using Cubit.Voxel.World;

namespace Cubit.Voxel.World;

/// <summary>世界配置资源：种子、生成类型、区块半径和垂直坐标契约，可 JSON 保存/加载。</summary>
public sealed class WorldConfig : Resource
{
    [Export("种子")]
    public uint Seed { get; set; } = 20260808;

    [Export("世界类型")]
    public WorldType WorldType { get; set; } = WorldType.Noise;

    [Export("生成半径")]
    public int GenerateRadius { get; set; } = VoxelWorld.GenerateRadius;

    /// <summary>世界最低方块 Y；必须按 16 高 Section 对齐。</summary>
    [Export("最低方块 Y")]
    public int MinY { get; set; } = VoxelVerticalBounds.DefaultMinY;

    /// <summary>世界垂直高度；必须是 16 的倍数。</summary>
    [Export("世界高度")]
    public int Height { get; set; } = VoxelVerticalBounds.DefaultHeight;

    /// <summary>最高方块 Y（不包含）。读取配置时通过 GetVerticalBounds 执行完整校验。</summary>
    public int MaxYExclusive => checked(MinY + Height);

    /// <summary>读取并校验项目声明的垂直坐标契约。</summary>
    public VoxelVerticalBounds GetVerticalBounds() => new(MinY, Height);
}
