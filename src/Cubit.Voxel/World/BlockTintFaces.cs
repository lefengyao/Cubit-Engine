namespace Cubit.Voxel.World;

/// <summary>方块色调应用到哪些立方体面；位序与 ChunkMesher 的六个面一致。</summary>
[Flags]
public enum BlockTintFaces : byte
{
    None = 0,
    Top = 1 << 0,
    Bottom = 1 << 1,
    PositiveZ = 1 << 2,
    NegativeZ = 1 << 3,
    PositiveX = 1 << 4,
    NegativeX = 1 << 5,
    Sides = PositiveZ | NegativeZ | PositiveX | NegativeX,
    All = Top | Bottom | Sides,
}
