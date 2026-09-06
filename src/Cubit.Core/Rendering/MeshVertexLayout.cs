namespace Cubit.Core.Rendering;

/// <summary>通用静态网格顶点布局；布局只描述 GPU 数据，不携带领域语义。</summary>
public enum MeshVertexLayout
{
    PositionUvShade,
    PositionUvShadeColor,
    PositionUvShadeTile,
}
