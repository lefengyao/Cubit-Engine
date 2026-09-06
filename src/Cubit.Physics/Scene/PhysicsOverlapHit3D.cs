namespace Cubit.Physics.Scene;

/// <summary>精确形状重叠的公开结果。</summary>
public readonly record struct PhysicsOverlapHit3D(
    CollisionObject3D Collider,
    CollisionShape3D CollisionShape,
    long ColliderId);
