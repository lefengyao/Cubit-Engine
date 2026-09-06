namespace Cubit.Voxel.World;

/// <summary>
/// 方块状态（借鉴 MC BlockState）：不可变属性包 = 方块 ID + 状态数据。
/// 存储时打包为 uint（低 16 位 = ID，高 16 位 = Data），支持与 ushort ID 隐式互转。
/// </summary>
public readonly struct BlockState : IEquatable<BlockState>
{
    public ushort Id { get; }

    public ushort Data { get; }

    public BlockState(ushort id, ushort data = 0)
    {
        Id = id;
        Data = data;
    }

    public bool IsAir => Id == BlockRegistry.Air;

    public static BlockState Air => new(BlockRegistry.Air);

    public uint Packed => (uint)(Id | (Data << 16));

    public static BlockState FromPacked(uint packed) => new((ushort)(packed & 0xFFFF), (ushort)(packed >> 16));

    public static implicit operator BlockState(ushort id) => new(id);

    public static implicit operator ushort(BlockState state) => state.Id;

    public bool Equals(BlockState other) => Id == other.Id && Data == other.Data;

    public override bool Equals(object? obj) => obj is BlockState other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Id, Data);

    public static bool operator ==(BlockState a, BlockState b) => a.Equals(b);

    public static bool operator !=(BlockState a, BlockState b) => !a.Equals(b);

    public override string ToString() => $"BlockState({BlockRegistry.GetName(Id)}, data={Data})";
}