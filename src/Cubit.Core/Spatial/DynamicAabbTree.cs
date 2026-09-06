using System.Numerics;
using Cubit.Core.Rendering;

namespace Cubit.Core.Spatial;

/// <summary>
/// 通用动态 AABB Tree。第一版要求所有访问在创建线程完成，后台 Job 需要由调用方提供快照。
/// 内部 fat AABB 只用于剪枝；公共查询始终用实际 AABB 复验叶子。
/// </summary>
public sealed class DynamicAabbTree<T> where T : notnull
{
    private const int NullNode = -1;

    private struct TreeNode
    {
        public Aabb3 Bounds;
        public Aabb3 ActualBounds;
        public T? Value;
        public int Parent;
        public int Left;
        public int Right;
        public int Height;
        public int Next;
        public int Generation;

        public readonly bool IsLeaf => Left == NullNode;
    }

    private readonly int _ownerThreadId;
    private readonly float _fatMargin;
    private readonly List<int> _queryStack = [];
    private TreeNode[] _nodes;
    private int _root = NullNode;
    private int _freeList;

    public DynamicAabbTree(float fatMargin = 0.1f)
    {
        if (!float.IsFinite(fatMargin) || fatMargin < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(fatMargin), "fat AABB 距离必须是非负有限数值");
        }

        _ownerThreadId = Environment.CurrentManagedThreadId;
        _fatMargin = fatMargin;
        _nodes = new TreeNode[16];
        InitializeNodeRange(0, _nodes.Length, invalidateExisting: false);
        _freeList = 0;
    }

    public int Count { get; private set; }

    /// <summary>插入一个用户值与其实际包围盒。</summary>
    public SpatialHandle Insert(T value, in Aabb3 bounds)
    {
        EnsureOwnerThread();
        ArgumentNullException.ThrowIfNull(value);

        var index = AllocateNode();
        var node = _nodes[index];
        node.Bounds = bounds.Inflated(_fatMargin);
        node.ActualBounds = bounds;
        node.Value = value;
        _nodes[index] = node;
        InsertLeaf(index);
        Count++;
        return new SpatialHandle(index, _nodes[index].Generation);
    }

    /// <summary>移除句柄对应的叶子；失效句柄返回 false。</summary>
    public bool Remove(SpatialHandle handle)
    {
        EnsureOwnerThread();
        if (!TryGetLeaf(handle, out var index))
        {
            return false;
        }

        RemoveLeaf(index);
        FreeNode(index);
        Count--;
        return true;
    }

    /// <summary>
    /// 更新实际包围盒。返回 true 表示新 bounds 超出 fat AABB，树已重插入；返回 false 仍会保存新的实际 bounds。
    /// </summary>
    public bool Update(SpatialHandle handle, in Aabb3 bounds)
    {
        EnsureOwnerThread();
        if (!TryGetLeaf(handle, out var index))
        {
            return false;
        }

        var node = _nodes[index];
        node.ActualBounds = bounds;
        _nodes[index] = node;
        if (node.Bounds.Contains(bounds))
        {
            return false;
        }

        RemoveLeaf(index);
        node = _nodes[index];
        node.Bounds = bounds.Inflated(_fatMargin);
        _nodes[index] = node;
        InsertLeaf(index);
        return true;
    }

    /// <summary>读取叶子当前实际包围盒和值。</summary>
    public bool TryGet(SpatialHandle handle, out SpatialQueryHit<T> hit)
    {
        EnsureOwnerThread();
        if (!TryGetLeaf(handle, out var index))
        {
            hit = default;
            return false;
        }

        var node = _nodes[index];
        hit = new SpatialQueryHit<T>(handle, node.Value!, node.ActualBounds);
        return true;
    }

    /// <summary>追加实际与查询范围相交的叶子，并返回新增数量。</summary>
    public int Query(in Aabb3 bounds, List<SpatialQueryHit<T>> results)
    {
        EnsureOwnerThread();
        ArgumentNullException.ThrowIfNull(results);
        var resultStart = results.Count;
        if (_root == NullNode)
        {
            return 0;
        }

        _queryStack.Clear();
        _queryStack.Add(_root);
        while (_queryStack.Count > 0)
        {
            var stackIndex = _queryStack.Count - 1;
            var index = _queryStack[stackIndex];
            _queryStack.RemoveAt(stackIndex);
            var node = _nodes[index];
            if (!node.Bounds.Intersects(bounds))
            {
                continue;
            }

            if (node.IsLeaf)
            {
                if (node.ActualBounds.Intersects(bounds))
                {
                    results.Add(new SpatialQueryHit<T>(new SpatialHandle(index, node.Generation), node.Value!, node.ActualBounds));
                }

                continue;
            }

            _queryStack.Add(node.Left);
            _queryStack.Add(node.Right);
        }

        return results.Count - resultStart;
    }

    /// <summary>追加实际与视锥相交的叶子，并返回新增数量。</summary>
    public int Query(in Frustum frustum, List<SpatialQueryHit<T>> results)
    {
        EnsureOwnerThread();
        ArgumentNullException.ThrowIfNull(results);
        var resultStart = results.Count;
        if (_root == NullNode)
        {
            return 0;
        }

        _queryStack.Clear();
        _queryStack.Add(_root);
        while (_queryStack.Count > 0)
        {
            var stackIndex = _queryStack.Count - 1;
            var index = _queryStack[stackIndex];
            _queryStack.RemoveAt(stackIndex);
            var node = _nodes[index];
            if (!frustum.Intersects(node.Bounds.Min, node.Bounds.Max))
            {
                continue;
            }

            if (node.IsLeaf)
            {
                if (frustum.Intersects(node.ActualBounds.Min, node.ActualBounds.Max))
                {
                    results.Add(new SpatialQueryHit<T>(new SpatialHandle(index, node.Generation), node.Value!, node.ActualBounds));
                }

                continue;
            }

            _queryStack.Add(node.Left);
            _queryStack.Add(node.Right);
        }

        return results.Count - resultStart;
    }

    /// <summary>返回最大距离内最近的实际 AABB 命中；距离相同按句柄索引稳定决胜。</summary>
    public bool Raycast(in Vector3 origin, in Vector3 direction, float maxDistance, out SpatialRayHit<T> hit)
    {
        EnsureOwnerThread();
        Aabb3.ValidateRay(origin, direction, maxDistance);
        hit = default;
        if (_root == NullNode)
        {
            return false;
        }

        var found = false;
        var bestDistance = maxDistance;
        _queryStack.Clear();
        _queryStack.Add(_root);
        while (_queryStack.Count > 0)
        {
            var stackIndex = _queryStack.Count - 1;
            var index = _queryStack[stackIndex];
            _queryStack.RemoveAt(stackIndex);
            var node = _nodes[index];
            if (!node.Bounds.RaycastUnchecked(origin, direction, bestDistance, out _))
            {
                continue;
            }

            if (node.IsLeaf)
            {
                if (!node.ActualBounds.RaycastUnchecked(origin, direction, bestDistance, out var distance))
                {
                    continue;
                }

                if (!found || distance < bestDistance || (distance == bestDistance && index < hit.Handle.Index))
                {
                    bestDistance = distance;
                    hit = new SpatialRayHit<T>(new SpatialHandle(index, node.Generation), node.Value!, node.ActualBounds, distance);
                    found = true;
                }

                continue;
            }

            _queryStack.Add(node.Left);
            _queryStack.Add(node.Right);
        }

        return found;
    }

    /// <summary>清空全部叶子并使先前句柄失效。</summary>
    public void Clear()
    {
        EnsureOwnerThread();
        _root = NullNode;
        Count = 0;
        _queryStack.Clear();
        InitializeNodeRange(0, _nodes.Length, invalidateExisting: true);
        _freeList = 0;
    }

    private int AllocateNode()
    {
        if (_freeList == NullNode)
        {
            var previousLength = _nodes.Length;
            Array.Resize(ref _nodes, previousLength * 2);
            InitializeNodeRange(previousLength, _nodes.Length, invalidateExisting: false);
            _freeList = previousLength;
        }

        var index = _freeList;
        var node = _nodes[index];
        _freeList = node.Next;
        node.Parent = NullNode;
        node.Left = NullNode;
        node.Right = NullNode;
        node.Height = 0;
        node.Next = NullNode;
        node.Bounds = default;
        node.ActualBounds = default;
        node.Value = default;
        _nodes[index] = node;
        return index;
    }

    private void FreeNode(int index)
    {
        var generation = NextGeneration(_nodes[index].Generation);
        _nodes[index] = new TreeNode
        {
            Parent = NullNode,
            Left = NullNode,
            Right = NullNode,
            Height = -1,
            Next = _freeList,
            Generation = generation,
        };
        _freeList = index;
    }

    private void InsertLeaf(int leaf)
    {
        if (_root == NullNode)
        {
            _root = leaf;
            var root = _nodes[leaf];
            root.Parent = NullNode;
            _nodes[leaf] = root;
            return;
        }

        var leafBounds = _nodes[leaf].Bounds;
        var index = _root;
        while (!_nodes[index].IsLeaf)
        {
            var node = _nodes[index];
            var left = _nodes[node.Left];
            var right = _nodes[node.Right];
            var area = node.Bounds.SurfaceArea;
            var combined = Aabb3.Combine(node.Bounds, leafBounds);
            var combinedArea = combined.SurfaceArea;
            var inheritanceCost = 2f * (combinedArea - area);
            var cost = 2f * combinedArea;
            var leftCombined = Aabb3.Combine(left.Bounds, leafBounds);
            var leftCost = (left.IsLeaf ? leftCombined.SurfaceArea : leftCombined.SurfaceArea - left.Bounds.SurfaceArea) + inheritanceCost;
            var rightCombined = Aabb3.Combine(right.Bounds, leafBounds);
            var rightCost = (right.IsLeaf ? rightCombined.SurfaceArea : rightCombined.SurfaceArea - right.Bounds.SurfaceArea) + inheritanceCost;
            if (cost < leftCost && cost < rightCost)
            {
                break;
            }

            index = leftCost < rightCost || (leftCost == rightCost && node.Left < node.Right) ? node.Left : node.Right;
        }

        var sibling = index;
        var oldParent = _nodes[sibling].Parent;
        var newParent = AllocateNode();
        var parentNode = _nodes[newParent];
        parentNode.Parent = oldParent;
        parentNode.Bounds = Aabb3.Combine(leafBounds, _nodes[sibling].Bounds);
        parentNode.Height = _nodes[sibling].Height + 1;
        parentNode.Left = sibling;
        parentNode.Right = leaf;
        _nodes[newParent] = parentNode;

        var siblingNode = _nodes[sibling];
        siblingNode.Parent = newParent;
        _nodes[sibling] = siblingNode;
        var leafNode = _nodes[leaf];
        leafNode.Parent = newParent;
        _nodes[leaf] = leafNode;

        if (oldParent == NullNode)
        {
            _root = newParent;
        }
        else
        {
            var oldParentNode = _nodes[oldParent];
            if (oldParentNode.Left == sibling)
            {
                oldParentNode.Left = newParent;
            }
            else
            {
                oldParentNode.Right = newParent;
            }

            _nodes[oldParent] = oldParentNode;
        }

        FixUpwards(newParent);
    }

    private void RemoveLeaf(int leaf)
    {
        if (leaf == _root)
        {
            _root = NullNode;
            return;
        }

        var parent = _nodes[leaf].Parent;
        var grandParent = _nodes[parent].Parent;
        var sibling = _nodes[parent].Left == leaf ? _nodes[parent].Right : _nodes[parent].Left;
        if (grandParent == NullNode)
        {
            _root = sibling;
            var siblingNode = _nodes[sibling];
            siblingNode.Parent = NullNode;
            _nodes[sibling] = siblingNode;
            FreeNode(parent);
        }
        else
        {
            var grandParentNode = _nodes[grandParent];
            if (grandParentNode.Left == parent)
            {
                grandParentNode.Left = sibling;
            }
            else
            {
                grandParentNode.Right = sibling;
            }

            _nodes[grandParent] = grandParentNode;
            var siblingNode = _nodes[sibling];
            siblingNode.Parent = grandParent;
            _nodes[sibling] = siblingNode;
            FreeNode(parent);
            FixUpwards(grandParent);
        }

        var leafNode = _nodes[leaf];
        leafNode.Parent = NullNode;
        _nodes[leaf] = leafNode;
    }

    private void FixUpwards(int index)
    {
        while (index != NullNode)
        {
            index = Balance(index);
            var node = _nodes[index];
            var left = _nodes[node.Left];
            var right = _nodes[node.Right];
            node.Height = 1 + Math.Max(left.Height, right.Height);
            node.Bounds = Aabb3.Combine(left.Bounds, right.Bounds);
            _nodes[index] = node;
            index = node.Parent;
        }
    }

    private int Balance(int index)
    {
        var a = _nodes[index];
        if (a.IsLeaf || a.Height < 2)
        {
            return index;
        }

        var leftIndex = a.Left;
        var rightIndex = a.Right;
        var left = _nodes[leftIndex];
        var right = _nodes[rightIndex];
        var balance = right.Height - left.Height;
        if (balance > 1)
        {
            var rightLeftIndex = right.Left;
            var rightRightIndex = right.Right;
            var rightLeft = _nodes[rightLeftIndex];
            var rightRight = _nodes[rightRightIndex];
            right.Left = index;
            right.Parent = a.Parent;
            a.Parent = rightIndex;
            ReplaceParentChild(right.Parent, index, rightIndex);

            if (rightLeft.Height > rightRight.Height)
            {
                right.Right = rightLeftIndex;
                a.Right = rightRightIndex;
                rightRight.Parent = index;
                a.Bounds = Aabb3.Combine(left.Bounds, rightRight.Bounds);
                right.Bounds = Aabb3.Combine(a.Bounds, rightLeft.Bounds);
                a.Height = 1 + Math.Max(left.Height, rightRight.Height);
                right.Height = 1 + Math.Max(a.Height, rightLeft.Height);
                _nodes[rightRightIndex] = rightRight;
            }
            else
            {
                right.Right = rightRightIndex;
                a.Right = rightLeftIndex;
                rightLeft.Parent = index;
                a.Bounds = Aabb3.Combine(left.Bounds, rightLeft.Bounds);
                right.Bounds = Aabb3.Combine(a.Bounds, rightRight.Bounds);
                a.Height = 1 + Math.Max(left.Height, rightLeft.Height);
                right.Height = 1 + Math.Max(a.Height, rightRight.Height);
                _nodes[rightLeftIndex] = rightLeft;
            }

            _nodes[index] = a;
            _nodes[rightIndex] = right;
            return rightIndex;
        }

        if (balance < -1)
        {
            var leftLeftIndex = left.Left;
            var leftRightIndex = left.Right;
            var leftLeft = _nodes[leftLeftIndex];
            var leftRight = _nodes[leftRightIndex];
            left.Left = index;
            left.Parent = a.Parent;
            a.Parent = leftIndex;
            ReplaceParentChild(left.Parent, index, leftIndex);

            if (leftLeft.Height > leftRight.Height)
            {
                left.Right = leftLeftIndex;
                a.Left = leftRightIndex;
                leftRight.Parent = index;
                a.Bounds = Aabb3.Combine(right.Bounds, leftRight.Bounds);
                left.Bounds = Aabb3.Combine(a.Bounds, leftLeft.Bounds);
                a.Height = 1 + Math.Max(right.Height, leftRight.Height);
                left.Height = 1 + Math.Max(a.Height, leftLeft.Height);
                _nodes[leftRightIndex] = leftRight;
            }
            else
            {
                left.Right = leftRightIndex;
                a.Left = leftLeftIndex;
                leftLeft.Parent = index;
                a.Bounds = Aabb3.Combine(right.Bounds, leftLeft.Bounds);
                left.Bounds = Aabb3.Combine(a.Bounds, leftRight.Bounds);
                a.Height = 1 + Math.Max(right.Height, leftLeft.Height);
                left.Height = 1 + Math.Max(a.Height, leftRight.Height);
                _nodes[leftLeftIndex] = leftLeft;
            }

            _nodes[index] = a;
            _nodes[leftIndex] = left;
            return leftIndex;
        }

        return index;
    }

    private void ReplaceParentChild(int parent, int oldChild, int newChild)
    {
        if (parent == NullNode)
        {
            _root = newChild;
            return;
        }

        var parentNode = _nodes[parent];
        if (parentNode.Left == oldChild)
        {
            parentNode.Left = newChild;
        }
        else
        {
            parentNode.Right = newChild;
        }

        _nodes[parent] = parentNode;
    }

    private bool TryGetLeaf(SpatialHandle handle, out int index)
    {
        index = handle.Index;
        if (!handle.IsValid || (uint)index >= (uint)_nodes.Length)
        {
            return false;
        }

        var node = _nodes[index];
        return node.Height == 0 && node.IsLeaf && node.Generation == handle.Generation;
    }

    private void InitializeNodeRange(int start, int endExclusive, bool invalidateExisting)
    {
        for (var index = start; index < endExclusive; index++)
        {
            var generation = _nodes[index].Generation;
            if (generation <= 0)
            {
                generation = 1;
            }
            else if (invalidateExisting)
            {
                generation = NextGeneration(generation);
            }

            _nodes[index] = new TreeNode
            {
                Parent = NullNode,
                Left = NullNode,
                Right = NullNode,
                Height = -1,
                Next = index + 1 < endExclusive ? index + 1 : NullNode,
                Generation = generation,
            };
        }
    }

    private static int NextGeneration(int generation) => generation == int.MaxValue ? 1 : generation + 1;

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("DynamicAabbTree 第一版只能在创建它的主线程访问");
        }
    }
}

