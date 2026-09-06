using System.Numerics;
using Cubit.Core.Scene;

namespace Cubit.Animation;

/// <summary>通用层级骨骼部件；不包含角色、装备或具体动画语义。</summary>
public sealed class Bone3D : Node3D
{
    private int _ownerThreadId = Environment.CurrentManagedThreadId;

    public Skeleton3D? OwnerSkeleton { get; internal set; }

    /// <summary>骨骼局部变换；入树后只能由拥有 SceneTree 的线程写入。</summary>
    public new Transform3D Transform
    {
        get => base.Transform;
        set
        {
            EnsureOwnerThread();
            base.Transform = value;
        }
    }

    [Export("位置")]
    public new Vector3 Position
    {
        get => base.Position;
        set
        {
            EnsureOwnerThread();
            base.Position = value;
        }
    }

    [Export("旋转")]
    public new Quaternion Rotation
    {
        get => base.Rotation;
        set
        {
            EnsureOwnerThread();
            base.Rotation = value;
        }
    }

    protected override void EnterTree()
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    protected override void ExitTree()
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("Bone3D 只能由拥有它的主线程访问");
        }
    }
}
