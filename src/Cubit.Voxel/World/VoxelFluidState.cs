namespace Cubit.Voxel.World;

/// <summary>
/// 流体方块状态的通用编码：低四位为液位，bit 4 表示下落。
/// 静态地形生成仍只接受源状态；动态模拟可使用完整的液位与下落编码。
/// </summary>
public readonly record struct VoxelFluidState(byte Level, bool Falling)
{
    /// <summary>静态源流体的满液位。</summary>
    public const byte SourceLevel = 8;

    private const ushort LevelMask = 0x000F;
    private const ushort FallingMask = 0x0010;
    private const ushort KnownMask = LevelMask | FallingMask;

    /// <summary>创建满液位、非下落的静态源流体状态。</summary>
    public static BlockState Source(ushort blockId) => FromLevel(blockId, SourceLevel);

    /// <summary>从已注册流体方块及其通用状态数据创建方块状态。</summary>
    public static BlockState FromLevel(ushort blockId, byte level, bool falling = false)
    {
        if (!BlockRegistry.IsRegistered(blockId) || !BlockRegistry.IsFluid(blockId))
        {
            throw new InvalidDataException($"方块不是已注册流体: {blockId}");
        }

        if (level is < 1 or > SourceLevel)
        {
            throw new InvalidDataException($"流体液位必须在 1..{SourceLevel}: {level}");
        }

        var data = (ushort)(level | (falling ? FallingMask : 0));
        return new BlockState(blockId, data);
    }

    /// <summary>尝试解码已注册流体的状态数据，拒绝未知位和非流体方块。</summary>
    public static bool TryDecode(BlockState state, out VoxelFluidState value)
    {
        value = default;
        if (!BlockRegistry.IsRegistered(state.Id) || !BlockRegistry.IsFluid(state.Id) ||
            (state.Data & ~KnownMask) != 0)
        {
            return false;
        }

        var level = (byte)(state.Data & LevelMask);
        if (level is < 1 or > SourceLevel)
        {
            return false;
        }

        value = new VoxelFluidState(level, (state.Data & FallingMask) != 0);
        return true;
    }

    /// <summary>验证 W1 静态流体不会意外接受 W2 的流动状态。</summary>
    public static void ValidateStaticSource(BlockState state)
    {
        if (!TryDecode(state, out var value) || value.Level != SourceLevel || value.Falling)
        {
            throw new InvalidDataException($"W1 只接受静态源流体状态: {state}");
        }
    }
}
