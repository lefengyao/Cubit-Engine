namespace Cubit.Voxel.World;

/// <summary>方块的通用网格形状；玩法和内容由调用项目自行定义。</summary>
public enum BlockRenderShape
{
    /// <summary>六面体方块。</summary>
    Cube,

    /// <summary>两组双面的交叉竖直平面。</summary>
    Cross,

    /// <summary>由体素插件独立网格化的流体体积。</summary>
    Fluid,
}
