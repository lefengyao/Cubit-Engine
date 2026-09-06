using System.Numerics;
using Cubit.Core.Rendering;

namespace Cubit.Core.Spatial;

/// <summary>静态 BVH 的输入项：索引由构造时的输入顺序决定。</summary>
public readonly record struct StaticBvhItem<T>(T Value, Aabb3 Bounds) where T : notnull;

/// <summary>静态 BVH 的范围或视锥查询命中。</summary>
public readonly record struct StaticBvhQueryHit<T>(int Index, T Value, Aabb3 Bounds);

/// <summary>静态 BVH 的最近射线命中。</summary>
public readonly record struct StaticBvhRayHit<T>(int Index, T Value, Aabb3 Bounds, float Distance);

/// <summary>
/// 通用静态 AABB BVH。构造后不再修改，可由多个调用方用各自的结果列表并发查询。
/// 它仅负责对象包围盒粗查；动态对象、三角形求交和世界流式属于其他独立能力。
/// </summary>
public sealed class StaticBvh<T> where T : notnull
{
    private const int NullNode = -1;
    private readonly StaticBvhItem<T>[] _items;
    private readonly int[] _order;
    private readonly TreeNode[] _nodes;
    private readonly int _root;
    private readonly int _maxLeafItems;
    private int _nodeCount;

    private struct TreeNode
    {
        public Aabb3 Bounds;
        public int Left;
        public int Right;
        public int Start;
        public int Count;

        public readonly bool IsLeaf => Count > 0;
    }

    public StaticBvh(IReadOnlyList<StaticBvhItem<T>> items, int maxLeafItems = 4)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (maxLeafItems is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLeafItems), "静态 BVH 叶子容量必须在 1 到 64 之间");
        }

        _maxLeafItems = maxLeafItems;
        _items = new StaticBvhItem<T>[items.Count];
        _order = new int[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            ArgumentNullException.ThrowIfNull(item.Value);
            _items[index] = item;
            _order[index] = index;
        }

        if (_items.Length == 0)
        {
            _nodes = [];
            _root = NullNode;
            return;
        }

        _nodes = new TreeNode[_items.Length * 2 - 1];
        _root = BuildNode(0, _items.Length);
    }

    public int Count => _items.Length;

    /// <summary>把与查询包围盒实际相交的项追加到调用方结果列表。</summary>
    public int Query(in Aabb3 bounds, List<StaticBvhQueryHit<T>> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return _root == NullNode ? 0 : QueryBounds(_root, bounds, results);
    }

    /// <summary>把与视锥相交的项追加到调用方结果列表。</summary>
    public int Query(in Frustum frustum, List<StaticBvhQueryHit<T>> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return _root == NullNode ? 0 : QueryFrustum(_root, frustum, results);
    }

    /// <summary>返回最近实际 AABB 命中；距离相同时使用较低的构造输入索引。</summary>
    public bool Raycast(in Vector3 origin, in Vector3 direction, float maxDistance, out StaticBvhRayHit<T> hit)
    {
        Aabb3.ValidateRay(origin, direction, maxDistance);
        if (_root == NullNode)
        {
            hit = default;
            return false;
        }

        var nearestDistance = maxDistance;
        var nearestIndex = int.MaxValue;
        var nearestHit = default(StaticBvhRayHit<T>);
        var found = RaycastNode(
            _root,
            origin,
            direction,
            ref nearestDistance,
            ref nearestIndex,
            ref nearestHit);
        hit = nearestHit;
        return found;
    }

    private int BuildNode(int start, int count)
    {
        var nodeIndex = _nodeCount++;
        var bounds = _items[_order[start]].Bounds;
        var firstCentroid = Center(bounds);
        var centroidMin = firstCentroid;
        var centroidMax = firstCentroid;
        for (var offset = 1; offset < count; offset++)
        {
            var itemBounds = _items[_order[start + offset]].Bounds;
            bounds = Aabb3.Combine(bounds, itemBounds);
            var centroid = Center(itemBounds);
            centroidMin = Vector3.Min(centroidMin, centroid);
            centroidMax = Vector3.Max(centroidMax, centroid);
        }

        if (count <= _maxLeafItems)
        {
            _nodes[nodeIndex] = new TreeNode
            {
                Bounds = bounds,
                Left = NullNode,
                Right = NullNode,
                Start = start,
                Count = count,
            };
            return nodeIndex;
        }

        var axis = LongestAxis(centroidMax - centroidMin);
        Array.Sort(_order, start, count, new ItemIndexComparer(_items, axis));
        var leftCount = count / 2;
        var left = BuildNode(start, leftCount);
        var right = BuildNode(start + leftCount, count - leftCount);
        _nodes[nodeIndex] = new TreeNode
        {
            Bounds = bounds,
            Left = left,
            Right = right,
            Start = 0,
            Count = 0,
        };
        return nodeIndex;
    }

    private int QueryBounds(int nodeIndex, in Aabb3 bounds, List<StaticBvhQueryHit<T>> results)
    {
        var node = _nodes[nodeIndex];
        if (!node.Bounds.Intersects(bounds))
        {
            return 0;
        }

        if (!node.IsLeaf)
        {
            return QueryBounds(node.Left, bounds, results) + QueryBounds(node.Right, bounds, results);
        }

        var appended = 0;
        for (var offset = 0; offset < node.Count; offset++)
        {
            var itemIndex = _order[node.Start + offset];
            var item = _items[itemIndex];
            if (item.Bounds.Intersects(bounds))
            {
                results.Add(new StaticBvhQueryHit<T>(itemIndex, item.Value, item.Bounds));
                appended++;
            }
        }

        return appended;
    }

    private int QueryFrustum(int nodeIndex, in Frustum frustum, List<StaticBvhQueryHit<T>> results)
    {
        var node = _nodes[nodeIndex];
        if (!frustum.Intersects(node.Bounds.Min, node.Bounds.Max))
        {
            return 0;
        }

        if (!node.IsLeaf)
        {
            return QueryFrustum(node.Left, frustum, results) + QueryFrustum(node.Right, frustum, results);
        }

        var appended = 0;
        for (var offset = 0; offset < node.Count; offset++)
        {
            var itemIndex = _order[node.Start + offset];
            var item = _items[itemIndex];
            if (frustum.Intersects(item.Bounds.Min, item.Bounds.Max))
            {
                results.Add(new StaticBvhQueryHit<T>(itemIndex, item.Value, item.Bounds));
                appended++;
            }
        }

        return appended;
    }

    private bool RaycastNode(
        int nodeIndex,
        in Vector3 origin,
        in Vector3 direction,
        ref float nearestDistance,
        ref int nearestIndex,
        ref StaticBvhRayHit<T> nearestHit)
    {
        var node = _nodes[nodeIndex];
        if (!node.Bounds.RaycastUnchecked(origin, direction, nearestDistance, out _))
        {
            return false;
        }

        if (node.IsLeaf)
        {
            var found = false;
            for (var offset = 0; offset < node.Count; offset++)
            {
                var itemIndex = _order[node.Start + offset];
                var item = _items[itemIndex];
                if (!item.Bounds.RaycastUnchecked(origin, direction, nearestDistance, out var distance))
                {
                    continue;
                }

                if (distance < nearestDistance || (distance == nearestDistance && itemIndex < nearestIndex))
                {
                    nearestDistance = distance;
                    nearestIndex = itemIndex;
                    nearestHit = new StaticBvhRayHit<T>(itemIndex, item.Value, item.Bounds, distance);
                    found = true;
                }
            }

            return found;
        }

        var left = _nodes[node.Left];
        var right = _nodes[node.Right];
        var leftHits = left.Bounds.RaycastUnchecked(origin, direction, nearestDistance, out var leftDistance);
        var rightHits = right.Bounds.RaycastUnchecked(origin, direction, nearestDistance, out var rightDistance);
        if (!leftHits && !rightHits)
        {
            return false;
        }

        var first = node.Left;
        var second = node.Right;
        if (!leftHits || (rightHits && (rightDistance < leftDistance || (rightDistance == leftDistance && node.Right < node.Left))))
        {
            first = node.Right;
            second = node.Left;
        }

        var foundFirst = (first == node.Left ? leftHits : rightHits) &&
            RaycastNode(first, origin, direction, ref nearestDistance, ref nearestIndex, ref nearestHit);
        var foundSecond = (second == node.Left ? leftHits : rightHits) &&
            RaycastNode(second, origin, direction, ref nearestDistance, ref nearestIndex, ref nearestHit);
        return foundFirst || foundSecond;
    }

    private static Vector3 Center(in Aabb3 bounds) => (bounds.Min + bounds.Max) * 0.5f;

    private static int LongestAxis(in Vector3 extent)
    {
        if (extent.Y > extent.X && extent.Y >= extent.Z)
        {
            return 1;
        }

        return extent.Z > extent.X && extent.Z > extent.Y ? 2 : 0;
    }

    private static float AxisValue(in Vector3 value, int axis) => axis switch
    {
        0 => value.X,
        1 => value.Y,
        _ => value.Z,
    };

    private sealed class ItemIndexComparer : IComparer<int>
    {
        private readonly StaticBvhItem<T>[] _items;
        private readonly int _axis;

        public ItemIndexComparer(StaticBvhItem<T>[] items, int axis)
        {
            _items = items;
            _axis = axis;
        }

        public int Compare(int left, int right)
        {
            var comparison = AxisValue(Center(_items[left].Bounds), _axis).CompareTo(AxisValue(Center(_items[right].Bounds), _axis));
            return comparison != 0 ? comparison : left.CompareTo(right);
        }
    }
}
