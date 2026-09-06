using System.Numerics;

namespace Cubit.Core.Scene;

/// <summary>带有可组合三维变换的通用场景节点。</summary>
public class Node3D : Node
{
    private Transform3D _transform = Transform3D.Identity;

    public Transform3D Transform
    {
        get => _transform;
        set
        {
            Transform3D.Validate(value);
            _transform = value;
        }
    }

    [Export("位置")]
    public Vector3 Position
    {
        get => _transform.Position;
        set => Transform = _transform.WithPosition(value);
    }

    public Quaternion Rotation
    {
        get => _transform.Rotation;
        set => Transform = _transform.WithRotation(value);
    }

    public float Scale
    {
        get => _transform.Scale;
        set => Transform = _transform.WithScale(value);
    }

    public Transform3D GlobalTransform => Parent is Node3D parent
        ? Transform3D.Combine(Transform, parent.GlobalTransform)
        : Transform;

    public Vector3 GlobalPosition => GlobalTransform.Position;
}
