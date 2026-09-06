using System.Numerics;
using Cubit.Core.Scene;
using Cubit.Physics.Shapes;

namespace Cubit.Physics.Scene;

/// <summary>可由场景文件导出的基础碰撞形状类型。</summary>
public enum CollisionShapeKind
{
    Box,
    Sphere,
    Capsule,
}

/// <summary>挂在碰撞对象下的形状节点。</summary>
public sealed class CollisionShape3D : Node3D
{
    /// <summary>程序化创建时可直接提供形状，优先级高于场景导出配置。</summary>
    public Shape3D? Shape { get; set; }

    [Export("形状类型")]
    public CollisionShapeKind ShapeKind { get; set; } = CollisionShapeKind.Box;

    [Export("盒尺寸")]
    public Vector3 BoxSize { get; set; } = Vector3.One;

    [Export("球半径")]
    public float SphereRadius { get; set; } = 0.5f;

    [Export("胶囊半径")]
    public float CapsuleRadius { get; set; } = 0.5f;

    [Export("胶囊圆柱高度")]
    public float CapsuleCylinderHeight { get; set; } = 1f;

    /// <summary>物理世界分配的稳定碰撞器 ID；写入仍由 PhysicsWorld3D 控制。</summary>
    public long ColliderId { get; internal set; }

    protected override void Ready()
    {
        Shape ??= ShapeKind switch
        {
            CollisionShapeKind.Box => new BoxShape3D { Size = BoxSize },
            CollisionShapeKind.Sphere => new SphereShape3D { Radius = SphereRadius },
            CollisionShapeKind.Capsule => new CapsuleShape3D
            {
                Radius = CapsuleRadius,
                CylinderHeight = CapsuleCylinderHeight,
            },
            _ => throw new InvalidOperationException($"未知碰撞形状类型: {ShapeKind}"),
        };
    }
}
