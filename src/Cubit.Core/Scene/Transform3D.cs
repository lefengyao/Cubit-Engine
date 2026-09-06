using System.Numerics;

namespace Cubit.Core.Scene;

/// <summary>
/// 通用三维变换。第一版只支持正的统一缩放，避免父旋转与非统一缩放组合产生剪切语义。
/// </summary>
public readonly struct Transform3D
{
    /// <summary>以单位变换创建的恒等变换。</summary>
    public static Transform3D Identity => new(Vector3.Zero, Quaternion.Identity, 1f);

    public Transform3D(Vector3 position, Quaternion rotation, float scale)
    {
        Validate(position, rotation, scale);
        Position = position;
        Rotation = Quaternion.Normalize(rotation);
        Scale = scale;
    }

    public Vector3 Position { get; }

    public Quaternion Rotation { get; }

    public float Scale { get; }

    /// <summary>检查可能由 C# default 创建的变换是否满足运行时不变量。</summary>
    public static bool IsValid(in Transform3D transform)
    {
        var rotationLengthSquared = transform.Rotation.LengthSquared();
        return IsFinite(transform.Position) && IsFinite(transform.Rotation) &&
            float.IsFinite(rotationLengthSquared) && rotationLengthSquared > float.Epsilon &&
            float.IsFinite(transform.Scale) && transform.Scale > 0f;
    }

    /// <summary>供渲染和数学查询使用的本地到父级矩阵。</summary>
    public Matrix4x4 Matrix =>
        Matrix4x4.CreateScale(Scale) *
        Matrix4x4.CreateFromQuaternion(Rotation) *
        Matrix4x4.CreateTranslation(Position);

    public Transform3D WithPosition(Vector3 position) => new(position, Rotation, Scale);

    public Transform3D WithRotation(Quaternion rotation) => new(Position, rotation, Scale);

    public Transform3D WithScale(float scale) => new(Position, Rotation, scale);

    /// <summary>把 local 变换组合到 parent 变换下。</summary>
    public static Transform3D Combine(in Transform3D local, in Transform3D parent)
    {
        Validate(local);
        Validate(parent);

        var position = parent.Position + Vector3.Transform(local.Position * parent.Scale, parent.Rotation);
        var rotation = Quaternion.Concatenate(local.Rotation, parent.Rotation);
        return new Transform3D(position, rotation, local.Scale * parent.Scale);
    }

    internal static void Validate(in Transform3D transform) =>
        Validate(transform.Position, transform.Rotation, transform.Scale);

    private static void Validate(Vector3 position, Quaternion rotation, float scale)
    {
        if (!IsFinite(position))
        {
            throw new ArgumentException("Transform3D 的位置必须为有限值", nameof(position));
        }

        var rotationLengthSquared = rotation.LengthSquared();
        if (!IsFinite(rotation) || !float.IsFinite(rotationLengthSquared) || rotationLengthSquared <= float.Epsilon)
        {
            throw new ArgumentException("Transform3D 的旋转必须为有效四元数", nameof(rotation));
        }

        if (!float.IsFinite(scale) || scale <= 0f)
        {
            throw new ArgumentException("Transform3D 的缩放必须为有限正数", nameof(scale));
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);
}
