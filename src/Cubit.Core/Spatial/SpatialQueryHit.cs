namespace Cubit.Core.Spatial;

/// <summary>范围或视锥查询的空间索引叶子。</summary>
public readonly record struct SpatialQueryHit<T>(SpatialHandle Handle, T Value, Aabb3 Bounds);

/// <summary>射线查询的最近空间索引叶子。</summary>
public readonly record struct SpatialRayHit<T>(SpatialHandle Handle, T Value, Aabb3 Bounds, float Distance);

