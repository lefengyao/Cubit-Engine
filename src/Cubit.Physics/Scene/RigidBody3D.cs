using System.Numerics;

namespace Cubit.Physics.Scene;

/// <summary>由物理世界在固定步中积分的线性刚体。</summary>
public sealed class RigidBody3D : CollisionObject3D
{
    private float _mass = 1f;
    private Vector3 _linearVelocity;

    /// <summary>刚体质量，必须为有限正数。</summary>
    public float Mass
    {
        get => _mass;
        set
        {
            World?.EnsureMainThreadAccess();
            if (!float.IsFinite(value) || value <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "质量必须为有限正数");
            }

            _mass = value;
        }
    }

    /// <summary>世界空间线速度。</summary>
    public Vector3 LinearVelocity
    {
        get => _linearVelocity;
        set
        {
            World?.EnsureMainThreadAccess();
            if (!IsFinite(value))
            {
                throw new ArgumentException("线速度必须为有限向量", nameof(value));
            }

            _linearVelocity = value;
        }
    }

    /// <summary>刚体是否已进入休眠。第一版仅由物理世界写入。</summary>
    public bool IsSleeping { get; internal set; }

    internal float InverseMass => 1f / _mass;

    internal int RestingTickCount { get; set; }

    /// <summary>将持续力排入下一固定步。</summary>
    public void ApplyCentralForce(Vector3 force) => World?.QueueForce(this, force);

    /// <summary>将瞬时冲量排入下一固定步。</summary>
    public void ApplyCentralImpulse(Vector3 impulse) => World?.QueueImpulse(this, impulse);

    internal void Wake()
    {
        IsSleeping = false;
        RestingTickCount = 0;
    }

    internal void Sleep()
    {
        IsSleeping = true;
        _linearVelocity = Vector3.Zero;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
