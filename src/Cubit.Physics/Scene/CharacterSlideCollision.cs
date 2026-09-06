using System.Numerics;

namespace Cubit.Physics.Scene;

/// <summary>CharacterBody3D 单次扫掠产生的稳定碰撞快照。</summary>
public readonly record struct CharacterSlideCollision(
    CollisionObject3D Collider,
    CollisionShape3D ColliderShape,
    Vector3 Position,
    Vector3 Normal,
    long ColliderId);
