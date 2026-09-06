namespace Cubit.Physics.Scene;

/// <summary>仅检测重叠关系的区域节点；事件在后续阶段延迟派发。</summary>
public sealed class Area3D : CollisionObject3D
{
    /// <summary>其他碰撞体进入区域后在固定步末尾触发。</summary>
    public event Action<CollisionObject3D>? BodyEntered;

    /// <summary>其他碰撞体离开区域后在固定步末尾触发。</summary>
    public event Action<CollisionObject3D>? BodyExited;

    internal void EmitBodyEntered(CollisionObject3D body) => BodyEntered?.Invoke(body);

    internal void EmitBodyExited(CollisionObject3D body) => BodyExited?.Invoke(body);
}
