namespace Cubit.Voxel.World;

/// <summary>
/// 区块分区（借鉴 MC Section）：16×16×16 方块存储，自适应调色板。
/// 状态 ≤256 种用 byte 索引（约 4KB）；超过则退化为 uint 直存（16KB）。
/// 全空气分区不分配任何数组（占内存接近 0）。
/// </summary>
public sealed class Section
{
    public const int SizeX = 16;
    public const int SizeY = 16;
    public const int SizeZ = 16;
    private const int BlockCount = SizeX * SizeY * SizeZ;
    private const int MaxPaletteSize = 256;

    private List<uint>? _palette; // 打包 BlockState（低16=Id，高16=Data）
    private byte[]? _index8;      // 调色板模式索引
    private uint[]? _direct;      // 直存模式（调色板超限）
    private byte[]? _light;       // 光照：高 4 位=天空光，低 4 位=方块光（懒分配）
    private bool _dirty;

    public int PaletteCount => _direct is not null ? BlockCount : _palette?.Count ?? 0;

    /// <summary>Section 自上次显式清除后是否发生过方块或光照变化。</summary>
    public bool IsDirty => _dirty;

    /// <summary>清除当前 Section 的持久化脏标记，不修改方块和光照数据。</summary>
    public void ClearDirty() => _dirty = false;

    /// <summary>标记 Section 需要持久化；仅供光照事务在最终值发生变化后调用。</summary>
    internal void MarkDirty() => _dirty = true;

    /// <summary>返回光照通道双指纹，供光照事务快速判断最终值是否发生变化。</summary>
    internal (ulong A, ulong B) GetLightHash()
    {
        if (_light is null)
        {
            return (0, 0);
        }

        var first = 14695981039346656037UL;
        var second = 1099511628211UL;
        var hasNonZero = false;
        for (var index = 0; index < _light.Length; index++)
        {
            var value = _light[index];
            hasNonZero |= value != 0;
            first ^= value;
            first *= 1099511628211UL;
            second ^= (byte)(value + index);
            second *= 14029467366897019727UL;
        }

        return hasNonZero ? (first, second) : (0, 0);
    }

    public (byte Sky, byte Block) GetLight(int x, int y, int z)
    {
        if (_light is null)
        {
            return (0, 0);
        }

        var v = _light[(y * SizeZ + z) * SizeX + x];
        return ((byte)(v >> 4), (byte)(v & 0xF));
    }

    public void SetLight(int x, int y, int z, byte sky, byte block) =>
        SetLight(x, y, z, sky, block, trackDirty: true);

    internal void SetLight(int x, int y, int z, byte sky, byte block, bool trackDirty)
    {
        var i = (y * SizeZ + z) * SizeX + x;
        var value = (byte)((sky << 4) | (block & 0xF));
        if ((_light is null ? (byte)0 : _light[i]) == value)
        {
            return;
        }

        _light ??= new byte[BlockCount];
        _light[i] = value;
        if (trackDirty)
        {
            _dirty = true;
        }
    }

    public BlockState Get(int x, int y, int z)
    {
        var i = (y * SizeZ + z) * SizeX + x;
        if (_direct is not null)
        {
            return BlockState.FromPacked(_direct[i]);
        }

        if (_index8 is not null)
        {
            return BlockState.FromPacked(_palette![_index8[i]]);
        }

        return BlockState.Air;
    }

    public void Set(int x, int y, int z, BlockState state)
    {
        var i = (y * SizeZ + z) * SizeX + x;
        var packed = state.Packed;
        var previous = _direct is not null
            ? _direct[i]
            : _index8 is not null
                ? _palette![_index8[i]]
                : BlockState.Air.Packed;
        if (previous == packed)
        {
            return;
        }

        if (_direct is not null)
        {
            _direct[i] = packed;
            _dirty = true;
            return;
        }

        if (_index8 is null)
        {
            _palette = [BlockState.Air.Packed];
            _index8 = new byte[BlockCount]; // 全 0 = Air
        }

        var idx = _palette!.IndexOf(packed);
        if (idx < 0)
        {
            if (_palette.Count < MaxPaletteSize)
            {
                idx = _palette.Count;
                _palette.Add(packed);
            }
            else
            {
                // 调色板超限：退化为直存
                _direct = new uint[BlockCount];
                for (var k = 0; k < BlockCount; k++)
                {
                    _direct[k] = _palette[_index8![k]];
                }

                _direct[i] = packed;
                _palette = null;
                _index8 = null;
                _dirty = true;
                return;
            }
        }

        _index8![i] = (byte)idx;
        _dirty = true;
    }

    /// <summary>复制可持久化数据和脏位；调用方需在持有所属 ChunkStore 锁时调用。</summary>
    internal Section CloneForPersistence() => new()
    {
        _palette = _palette is null ? null : [.. _palette],
        _index8 = _index8?.ToArray(),
        _direct = _direct?.ToArray(),
        _light = _light?.ToArray(),
        _dirty = _dirty,
    };

    /// <summary>比较方块和光照的持久化内容，不把脏位作为内容差异。</summary>
    internal bool HasSamePersistenceContent(Section other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return SameSequence(_palette, other._palette) &&
            SameSequence(_index8, other._index8) &&
            SameSequence(_direct, other._direct) &&
            SameSequence(_light, other._light);
    }

    public void Save(BinaryWriter writer)
    {
        if (_direct is not null)
        {
            writer.Write((byte)1);
            for (var i = 0; i < BlockCount; i++)
            {
                writer.Write(_direct[i]);
            }

            WriteLight(writer);
            return;
        }

        if (_palette is null)
        {
            writer.Write((byte)0);
            writer.Write(0); // 全空气
            WriteLight(writer);
            return;
        }

        writer.Write((byte)2); // 按位调色板索引
        writer.Write(_palette.Count);
        foreach (var packed in _palette)
        {
            writer.Write(packed);
        }

        var bits = BitsForPalette(_palette.Count);
        var packedIndices = PackIndices(_index8!, bits);
        writer.Write(bits);
        writer.Write(packedIndices.Length);
        writer.Write(packedIndices);
        WriteLight(writer);
    }

    private void WriteLight(BinaryWriter writer)
    {
        if (_light is null)
        {
            writer.Write((byte)0);
            return;
        }

        writer.Write((byte)1);
        writer.Write(_light.Length);
        writer.Write(_light);
    }

    private void ReadLight(BinaryReader reader)
    {
        var hasLight = reader.ReadByte();
        if (hasLight == 1)
        {
            var len = reader.ReadInt32();
            _light = reader.ReadBytes(len);
        }
    }

    public static Section Load(BinaryReader reader)
    {
        var section = new Section();
        var mode = reader.ReadByte();
        if (mode == 1)
        {
            section._direct = new uint[BlockCount];
            for (var i = 0; i < BlockCount; i++)
            {
                section._direct[i] = reader.ReadUInt32();
                ValidatePackedBlock(section._direct[i]);
            }

            section.ReadLight(reader);
            return section;
        }

        if (mode == 2)
        {
            var paletteCount = reader.ReadInt32();
            if (paletteCount <= 0 || paletteCount > MaxPaletteSize)
            {
                throw new InvalidDataException($"Section 位打包调色板数量无效: {paletteCount}");
            }

            section._palette = new List<uint>(paletteCount);
            for (var i = 0; i < paletteCount; i++)
            {
                section._palette.Add(reader.ReadUInt32());
                ValidatePackedBlock(section._palette[^1]);
            }

            var bits = reader.ReadByte();
            if (bits != BitsForPalette(paletteCount))
            {
                throw new InvalidDataException($"Section 位打包宽度与调色板不匹配: palette={paletteCount} bits={bits}");
            }

            var packedLength = reader.ReadInt32();
            var expectedLength = PackedIndexLength(bits);
            if (packedLength != expectedLength)
            {
                throw new InvalidDataException($"Section 位打包负载长度不一致: expected={expectedLength} actual={packedLength}");
            }

            var packedIndices = reader.ReadBytes(packedLength);
            if (packedIndices.Length != packedLength)
            {
                throw new InvalidDataException("Section 位打包负载截断");
            }

            section._index8 = UnpackIndices(packedIndices, bits);
            section.ReadLight(reader);
            return section;
        }

        var count = reader.ReadInt32();
        if (count > 0)
        {
            section._palette = new List<uint>(count);
            for (var i = 0; i < count; i++)
            {
                section._palette.Add(reader.ReadUInt32());
                ValidatePackedBlock(section._palette[^1]);
            }

            var len = reader.ReadInt32();
            section._index8 = reader.ReadBytes(len);
        }

        section.ReadLight(reader);
        return section;
    }

    private static void ValidatePackedBlock(uint packed)
    {
        var id = (ushort)(packed & 0xFFFF);
        if (!BlockRegistry.IsRegistered(id))
        {
            throw new InvalidDataException($"Section 包含未知方块 ID: {id}");
        }
    }

    private static byte BitsForPalette(int paletteCount)
    {
        var bits = 1;
        while ((1 << bits) < paletteCount)
        {
            bits++;
        }

        return (byte)bits;
    }

    private static int PackedIndexLength(int bits) => (BlockCount * bits + 7) / 8;

    private static byte[] PackIndices(byte[] indices, int bits)
    {
        var packed = new byte[PackedIndexLength(bits)];
        var mask = (1 << bits) - 1;
        for (var index = 0; index < indices.Length; index++)
        {
            var bitOffset = index * bits;
            var byteOffset = bitOffset >> 3;
            var shift = bitOffset & 7;
            var value = indices[index] & mask;
            packed[byteOffset] |= (byte)(value << shift);
            if (shift + bits > 8)
            {
                packed[byteOffset + 1] |= (byte)(value >> (8 - shift));
            }
        }

        return packed;
    }

    private static byte[] UnpackIndices(byte[] packed, int bits)
    {
        var indices = new byte[BlockCount];
        var mask = (1 << bits) - 1;
        for (var index = 0; index < indices.Length; index++)
        {
            var bitOffset = index * bits;
            var byteOffset = bitOffset >> 3;
            var shift = bitOffset & 7;
            var value = packed[byteOffset] >> shift;
            if (shift + bits > 8)
            {
                value |= packed[byteOffset + 1] << (8 - shift);
            }

            indices[index] = (byte)(value & mask);
        }

        return indices;
    }

    private static bool SameSequence<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }
}
