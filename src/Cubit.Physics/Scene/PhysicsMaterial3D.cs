using Cubit.Core.Scene;

namespace Cubit.Physics.Scene;

/// <summary>碰撞接触使用的摩擦与反弹参数。</summary>
public sealed class PhysicsMaterial3D : Resource
{
    private float _friction = 0.5f;
    private float _restitution;

    [Export("摩擦")]
    public float Friction
    {
        get => _friction;
        set
        {
            if (!float.IsFinite(value) || value < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "摩擦必须为有限非负数");
            }

            _friction = value;
        }
    }

    [Export("反弹")]
    public float Restitution
    {
        get => _restitution;
        set
        {
            if (!float.IsFinite(value) || value is < 0f or > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "反弹必须在 0 到 1 之间");
            }

            _restitution = value;
        }
    }
}
