using System.Numerics;

namespace Cubit.Physics.Scene;

/// <summary>精确射线命中的公开结果。</summary>
public readonly record struct PhysicsRayHit3D(
    CollisionObject3D Collider,
    CollisionShape3D CollisionShape,
    Vector3 Position,
    Vector3 Normal,
    float Distance,
    long ColliderId);
