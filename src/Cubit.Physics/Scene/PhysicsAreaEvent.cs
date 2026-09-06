namespace Cubit.Physics.Scene;

/// <summary>区域与碰撞体之间延迟派发的稳定重叠变更。</summary>
public readonly record struct PhysicsAreaEvent(
    Area3D Area,
    CollisionObject3D Other,
    bool Entered,
    long AreaColliderId,
    long OtherColliderId);
