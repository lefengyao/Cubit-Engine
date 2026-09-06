using Cubit.Core.Scene;

namespace Cubit.Physics.Scene;

/// <summary>可参与碰撞过滤的三维场景节点基类。</summary>
public abstract class CollisionObject3D : Node3D
{
    [Export("碰撞层")]
    public uint CollisionLayer { get; set; } = 1;

    [Export("碰撞掩码")]
    public uint CollisionMask { get; set; } = uint.MaxValue;

    /// <summary>接触求解使用的摩擦与弹性参数。</summary>
    public PhysicsMaterial3D? PhysicsMaterial { get; set; }

    internal PhysicsWorld3D? World { get; set; }
}
