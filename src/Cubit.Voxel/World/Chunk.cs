namespace Cubit.Voxel.World;

/// <summary>
/// 区块（借鉴 MC Chunk）：16×16×384 方块列，纵向划分为 24 个 16×16×16 的 Section。
/// 方块以 BlockState 存储，内存由 Section 自适应调色板管理。
/// </summary>
public sealed class Chunk
{
    public const int SizeX = 16;
    /// <summary>原版主世界最低方块 Y。</summary>
    public const int MinY = VoxelVerticalBounds.DefaultMinY;

    /// <summary>原版主世界最高边界（不包含）。</summary>
    public const int MaxYExclusive = VoxelVerticalBounds.DefaultMaxYExclusive;

    public const int SizeY = MaxYExclusive - MinY;
    public const int SizeZ = 16;
    public const int SectionSizeY = VoxelVerticalBounds.SectionSize;
    public const int SectionCount = VoxelVerticalBounds.DefaultHeight / VoxelVerticalBounds.SectionSize;
    public const int LegacySectionCount = 4;

    /// <summary>当前 Chunk 布局使用的默认垂直契约；自定义配置需先通过 WorldConfig 校验。</summary>
    public static VoxelVerticalBounds VerticalBounds => VoxelVerticalBounds.Default;

    private readonly Section[] _sections;

    public Chunk()
    {
        _sections = new Section[SectionCount];
        for (var i = 0; i < SectionCount; i++)
        {
            _sections[i] = new Section();
        }
    }

    public static bool IsValidY(int y) => VerticalBounds.Contains(y);

    public static int SectionIndex(int y) => (y - MinY) >> 4;

    public static int SectionLocalY(int y) => (y - MinY) & (SectionSizeY - 1);

    /// <summary>返回 24 个 Section 的持久化脏位图，最低位对应 Section 0。</summary>
    public ulong DirtySectionMask
    {
        get
        {
            var mask = 0UL;
            for (var index = 0; index < SectionCount; index++)
            {
                if (_sections[index].IsDirty)
                {
                    mask |= 1UL << index;
                }
            }

            return mask;
        }
    }

    /// <summary>清除指定 Section 的持久化脏标记；默认清除整个区块。</summary>
    public void ClearDirtySections(ulong mask = ulong.MaxValue)
    {
        for (var index = 0; index < SectionCount; index++)
        {
            if ((mask & (1UL << index)) != 0)
            {
                _sections[index].ClearDirty();
            }
        }
    }

    /// <summary>标记新生成或异常中断区块的全部 Section 需要持久化。</summary>
    internal void MarkAllDirtySections()
    {
        for (var index = 0; index < SectionCount; index++)
        {
            _sections[index].MarkDirty();
        }
    }

    public BlockState GetBlock(int x, int y, int z)
    {
        if ((uint)x >= SizeX || !IsValidY(y) || (uint)z >= SizeZ)
        {
            return BlockState.Air;
        }

        return _sections[SectionIndex(y)].Get(x, SectionLocalY(y), z);
    }

    public void SetBlock(int x, int y, int z, BlockState block)
    {
        if ((uint)x >= SizeX || !IsValidY(y) || (uint)z >= SizeZ)
        {
            return;
        }

        _sections[SectionIndex(y)].Set(x, SectionLocalY(y), z, block);
    }

    public Section GetSection(int index) => _sections[index];

    public void SetSection(int index, Section section) => _sections[index] = section;

    /// <summary>复制脱离世界的持久化快照；调用方必须持有 ChunkStore.SyncRoot。</summary>
    internal Chunk CloneForPersistence()
    {
        var clone = new Chunk();
        for (var index = 0; index < SectionCount; index++)
        {
            clone._sections[index] = _sections[index].CloneForPersistence();
        }

        return clone;
    }

    /// <summary>比较区块持久化内容，不把 Section 脏位作为内容差异。</summary>
    internal bool HasSamePersistenceContent(Chunk other)
    {
        ArgumentNullException.ThrowIfNull(other);
        for (var index = 0; index < SectionCount; index++)
        {
            if (!_sections[index].HasSamePersistenceContent(other._sections[index]))
            {
                return false;
            }
        }

        return true;
    }
}
