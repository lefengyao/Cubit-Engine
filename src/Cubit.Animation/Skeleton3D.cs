using Cubit.Core.Scene;

namespace Cubit.Animation;

/// <summary>通用骨骼容器；负责骨骼命名、父子关系和生命周期索引。</summary>
public sealed class Skeleton3D : Node3D
{
    private readonly List<Bone3D> _orderedBones = [];
    private readonly Dictionary<string, Bone3D> _bones = new(StringComparer.Ordinal);
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

    public IReadOnlyList<Bone3D> Bones => _orderedBones;

    public Bone3D? RootBone => _orderedBones.FirstOrDefault(bone => bone.Parent == this);

    /// <summary>骨骼必须通过显式 API 加入，避免普通 Node 子节点混入骨骼索引。</summary>
    public new void AddChild(Node child)
    {
        if (child is not Bone3D bone)
        {
            throw new InvalidOperationException("Skeleton3D 只能包含 Bone3D 子节点");
        }

        AddBone(bone);
    }

    public void AddBone(Bone3D bone, Bone3D? parent = null)
    {
        EnsureOwnerThread();
        ArgumentNullException.ThrowIfNull(bone);
        if (bone.OwnerSkeleton is not null && !ReferenceEquals(bone.OwnerSkeleton, this))
        {
            throw new InvalidOperationException("Bone3D 已属于另一个 Skeleton3D");
        }

        if (bone.Parent is not null)
        {
            throw new InvalidOperationException($"Bone3D 已有父节点: {bone.Name}");
        }

        if (string.IsNullOrWhiteSpace(bone.Name) || !_bones.TryAdd(bone.Name, bone))
        {
            throw new InvalidOperationException($"Skeleton3D 骨骼名称重复或为空: {bone.Name}");
        }

        if (parent is not null)
        {
            if (!ReferenceEquals(parent.OwnerSkeleton, this) || !_bones.ContainsKey(parent.Name))
            {
                _bones.Remove(bone.Name);
                throw new InvalidOperationException("骨骼父节点必须属于同一个 Skeleton3D");
            }

            if (ReferenceEquals(parent, bone) || ContainsDescendant(bone, parent))
            {
                _bones.Remove(bone.Name);
                throw new InvalidOperationException("Skeleton3D 拒绝循环骨骼层级");
            }
        }

        bone.OwnerSkeleton = this;
        if (parent is null)
        {
            base.AddChild(bone);
        }
        else
        {
            parent.AddChild(bone);
        }

        _orderedBones.Add(bone);
    }

    protected override void Ready()
    {
        EnsureOwnerThread();
        if (_orderedBones.Count > 0)
        {
            return;
        }

        foreach (var bone in Children.OfType<Bone3D>())
        {
            RegisterExistingBone(bone);
        }
    }

    protected override void ExitTree()
    {
        EnsureOwnerThread();
        foreach (var bone in _orderedBones)
        {
            bone.OwnerSkeleton = null;
        }

        _orderedBones.Clear();
        _bones.Clear();
    }

    private void RegisterExistingBone(Bone3D bone)
    {
        if (string.IsNullOrWhiteSpace(bone.Name) || !_bones.TryAdd(bone.Name, bone))
        {
            throw new InvalidOperationException($"Skeleton3D 骨骼名称重复或为空: {bone.Name}");
        }

        bone.OwnerSkeleton = this;
        _orderedBones.Add(bone);
        foreach (var child in bone.Children.OfType<Bone3D>())
        {
            RegisterExistingBone(child);
        }
    }

    private static bool ContainsDescendant(Node ancestor, Node candidate)
    {
        foreach (var child in ancestor.Children)
        {
            if (ReferenceEquals(child, candidate) || ContainsDescendant(child, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("Skeleton3D 只能由拥有它的主线程访问");
        }
    }
}
