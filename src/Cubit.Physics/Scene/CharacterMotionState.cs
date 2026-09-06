using System.Numerics;

namespace Cubit.Physics.Scene;

/// <summary>
/// 角色运动学的调用方拥有状态契约。
/// Position 是 CharacterBody3D 的局部位置；物理插件只使用作者化节点的碰撞形状和过滤配置。
/// </summary>
public struct CharacterMotionState
{
    public Vector3 Position;
    public Vector3 Velocity;
    public bool IsOnFloor;
    public int LastSlideCount;

    internal void Validate()
    {
        if (!IsFinite(Position) || !IsFinite(Velocity))
        {
            throw new ArgumentException("角色运动状态的位置和速度必须是有限向量");
        }
    }

    private static bool IsFinite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
