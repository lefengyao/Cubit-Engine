namespace Cubit.Core.Spatial;

/// <summary>动态空间索引的 generation 安全句柄。</summary>
public readonly record struct SpatialHandle(int Index, int Generation)
{
    public bool IsValid => Index >= 0 && Generation > 0;
}

