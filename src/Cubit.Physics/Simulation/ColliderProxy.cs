using Cubit.Core.Scene;
using Cubit.Core.Spatial;
using Cubit.Physics.Scene;

namespace Cubit.Physics.Simulation;

/// <summary>物理世界内部使用的形状快照，只保存宽相位所需数据。</summary>
internal sealed class ColliderProxy
{
    public ColliderProxy(
        long colliderId,
        CollisionObject3D collisionObject,
        CollisionShape3D collisionShape,
        Aabb3 bounds,
        Transform3D staticTransform)
    {
        ColliderId = colliderId;
        CollisionObject = collisionObject;
        CollisionShape = collisionShape;
        Bounds = bounds;
        StaticTransform = staticTransform;
    }

    public long ColliderId { get; }

    public CollisionObject3D CollisionObject { get; }

    public CollisionShape3D CollisionShape { get; }

    public Aabb3 Bounds { get; set; }

    public Transform3D StaticTransform { get; }

    public SpatialHandle DynamicHandle { get; set; }
}
