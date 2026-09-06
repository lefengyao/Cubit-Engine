namespace Cubit.Voxel.World;

/// <summary>体素 DDA 查询对已注册流体的命中策略；调用方决定玩法意图。</summary>
public enum VoxelRaycastFluidMode
{
    /// <summary>跳过全部流体，只命中其他非空气方块。</summary>
    None,

    /// <summary>只命中满液位、非下落的流体源。</summary>
    SourceOnly,

    /// <summary>命中任何编码有效的流体。</summary>
    Any,
}
