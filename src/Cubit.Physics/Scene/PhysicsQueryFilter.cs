namespace Cubit.Physics.Scene;

/// <summary>物理查询的层、掩码和自身排除条件。</summary>
public readonly struct PhysicsQueryFilter
{
    /// <summary>创建接受所有碰撞层的默认过滤器。</summary>
    public PhysicsQueryFilter()
        : this(uint.MaxValue, uint.MaxValue)
    {
    }

    public PhysicsQueryFilter(
        uint collisionMask = uint.MaxValue,
        uint collisionLayer = uint.MaxValue,
        CollisionObject3D? exclude = null)
    {
        CollisionMask = collisionMask;
        CollisionLayer = collisionLayer;
        Exclude = exclude;
        IsConfigured = true;
    }

    public uint CollisionMask { get; }

    public uint CollisionLayer { get; }

    public CollisionObject3D? Exclude { get; }

    /// <summary>区分 C# 的 default 值与显式全零层/掩码。</summary>
    internal bool IsConfigured { get; }
}
