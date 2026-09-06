using System.Numerics;

namespace Cubit.Physics.Scene;

/// <summary>由 PhysicsWorld3D 在固定步中执行扫掠滑动的运动学角色。</summary>
public class CharacterBody3D : CollisionObject3D
{
    internal const int MaximumSlideCount = 4;
    private readonly CharacterSlideCollision[] _slides = new CharacterSlideCollision[MaximumSlideCount];
    private Vector3 _upDirection = Vector3.UnitY;
    private float _floorMaxAngleCosine = 0.70710677f;
    private float _maxStepHeight;
    private Vector3 _velocity;

    /// <summary>下一次 MoveAndSlide 使用的世界空间速度。</summary>
    public Vector3 Velocity
    {
        get => _velocity;
        set
        {
            World?.EnsureMainThreadAccess();
            if (!IsFinite(value))
            {
                throw new ArgumentException("角色速度必须为有限向量", nameof(value));
            }

            _velocity = value;
        }
    }

    /// <summary>用于判定地面的向上方向，必须为有限非零向量。</summary>
    public Vector3 UpDirection
    {
        get => _upDirection;
        set
        {
            World?.EnsureMainThreadAccess();
            var lengthSquared = value.LengthSquared();
            if (!float.IsFinite(lengthSquared) || lengthSquared <= 1e-12f)
            {
                throw new ArgumentException("UpDirection 必须是有限非零向量", nameof(value));
            }

            _upDirection = Vector3.Normalize(value);
        }
    }

    /// <summary>接触法线与 UpDirection 点积达到此阈值时视为地面。</summary>
    public float FloorMaxAngleCosine
    {
        get => _floorMaxAngleCosine;
        set
        {
            World?.EnsureMainThreadAccess();
            if (!float.IsFinite(value) || value < -1f || value > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "地面余弦必须位于 [-1, 1]");
            }

            _floorMaxAngleCosine = value;
        }
    }

    /// <summary>
    /// 水平移动遇到低矮方块上棱时允许的最大上抬距离。零表示禁用跨步；
    /// 物理世界仍会在上抬前验证头顶与目标路径均没有碰撞。
    /// </summary>
    public float MaxStepHeight
    {
        get => _maxStepHeight;
        set
        {
            World?.EnsureMainThreadAccess();
            if (!float.IsFinite(value) || value < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "最大跨步高度必须是有限非负数");
            }

            _maxStepHeight = value;
        }
    }

    /// <summary>上一个已处理移动命令是否接触到地面。</summary>
    public bool IsOnFloor { get; internal set; }

    /// <summary>上一个已处理移动命令记录的接触数量，最大为四个。</summary>
    public int LastSlideCount { get; internal set; }

    /// <summary>排队移动；实际碰撞和位置变更只能由下一物理固定步完成。</summary>
    public void MoveAndSlide() => World?.QueueCharacterMove(this, Velocity);

    /// <summary>读取复用缓冲区中的接触快照。</summary>
    public CharacterSlideCollision GetSlideCollision(int index)
    {
        if ((uint)index >= (uint)LastSlideCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return _slides[index];
    }

    internal void BeginMove()
    {
        IsOnFloor = false;
        LastSlideCount = 0;
    }

    internal void AddSlide(in CharacterSlideCollision collision)
    {
        if (LastSlideCount < MaximumSlideCount)
        {
            _slides[LastSlideCount++] = collision;
        }
    }

    private static bool IsFinite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
