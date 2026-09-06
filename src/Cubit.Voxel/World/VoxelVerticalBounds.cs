namespace Cubit.Voxel.World;

/// <summary>
/// 体素世界的垂直坐标契约。
/// MinY 必须按 Section 对齐，Height 必须是 16 的倍数，避免坐标映射出现半个 Section。
/// </summary>
public readonly record struct VoxelVerticalBounds
{
    public const int SectionSize = 16;
    public const int DefaultMinY = -64;
    public const int DefaultHeight = 384;
    public const int DefaultMaxYExclusive = DefaultMinY + DefaultHeight;

    public VoxelVerticalBounds(int minY, int height)
    {
        if (minY % SectionSize != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minY), minY, "体素世界最低 Y 必须按 16 高 Section 对齐");
        }

        if (height <= 0 || height % SectionSize != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "体素世界高度必须是正数且为 16 的倍数");
        }

        _ = checked(minY + height);
        MinY = minY;
        Height = height;
    }

    public int MinY { get; }

    public int Height { get; }

    public int MaxYExclusive => checked(MinY + Height);

    public int SectionCount => Height / SectionSize;

    public static VoxelVerticalBounds Default => new(DefaultMinY, DefaultHeight);

    public bool Contains(int y) => y >= MinY && y < MaxYExclusive;
}
