using System.Numerics;

namespace Cubit.Core.Spatial;

/// <summary>规则空间分区的三维坐标。</summary>
public readonly record struct PartitionCell(long X, long Y, long Z);

/// <summary>以中心分区为基准的三维闭合半径。</summary>
public readonly record struct PartitionExtent(int X, int Y, int Z)
{
    public PartitionExtent(int uniformRadius)
        : this(uniformRadius, uniformRadius, uniformRadius)
    {
    }
}

/// <summary>进入分区的 generation 安全票据。</summary>
public readonly record struct PartitionTicket(PartitionCell Cell, long Generation)
{
    public bool IsValid => Generation > 0;
}

/// <summary>分区活动意图的变化类型。</summary>
public enum PartitionChangeKind
{
    Enter,
    Exit,
}

/// <summary>调用方应处理的分区进入或退出意图。</summary>
public readonly record struct PartitionChange(
    PartitionChangeKind Kind,
    PartitionTicket Ticket,
    long DistanceSquared);

/// <summary>
/// 通用规则空间分区。它只计算所需分区与异步结果票据，不拥有项目内容或调度后台任务。
/// 第一版所有公开状态访问均要求创建线程调用。
/// </summary>
public sealed class WorldPartition
{
    private const long MaxTargetCellCount = 1_000_000;
    private readonly int _ownerThreadId;
    private readonly Dictionary<PartitionCell, long> _desired = [];
    private long _nextGeneration;

    public WorldPartition(Vector3 cellSize)
    {
        ValidateCellSize(cellSize);
        CellSize = cellSize;
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>每个规则分区的世界空间尺寸。</summary>
    public Vector3 CellSize { get; }

    /// <summary>当前 Core 需要调用方保留的分区数量，不代表内容已加载。</summary>
    public int DesiredCount
    {
        get
        {
            EnsureOwnerThread();
            return _desired.Count;
        }
    }

    /// <summary>将有限世界坐标映射到包含该点的规则分区。</summary>
    public PartitionCell ToCell(in Vector3 worldPosition)
    {
        EnsureOwnerThread();
        ValidateFinite(worldPosition, nameof(worldPosition));
        return new PartitionCell(
            ToCoordinate(worldPosition.X, CellSize.X),
            ToCoordinate(worldPosition.Y, CellSize.Y),
            ToCoordinate(worldPosition.Z, CellSize.Z));
    }

    /// <summary>返回规则分区的实际世界空间包围盒。</summary>
    public Aabb3 GetCellBounds(PartitionCell cell)
    {
        EnsureOwnerThread();
        return new Aabb3(
            new Vector3(
                ToFiniteFloat((double)cell.X * CellSize.X, nameof(cell)),
                ToFiniteFloat((double)cell.Y * CellSize.Y, nameof(cell)),
                ToFiniteFloat((double)cell.Z * CellSize.Z, nameof(cell))),
            new Vector3(
                ToFiniteFloat((double)checked(cell.X + 1) * CellSize.X, nameof(cell)),
                ToFiniteFloat((double)checked(cell.Y + 1) * CellSize.Y, nameof(cell)),
                ToFiniteFloat((double)checked(cell.Z + 1) * CellSize.Z, nameof(cell))));
    }

    /// <summary>刷新观察范围，并把所需分区的进入和退出意图追加到调用方列表。</summary>
    public int Refresh(in Vector3 focusPosition, PartitionExtent extent, List<PartitionChange> changes)
    {
        EnsureOwnerThread();
        ValidateFinite(focusPosition, nameof(focusPosition));
        ValidateExtent(extent);
        ArgumentNullException.ThrowIfNull(changes);

        var center = ToCell(focusPosition);
        var target = BuildTarget(center, extent);
        var entering = target
            .Where(cell => !_desired.ContainsKey(cell))
            .Select(cell => new PendingChange(cell, 0, DistanceSquared(cell, center)))
            .OrderBy(change => change.DistanceSquared)
            .ThenBy(change => change.Cell.Z)
            .ThenBy(change => change.Cell.Y)
            .ThenBy(change => change.Cell.X)
            .ToArray();
        var exiting = _desired
            .Where(entry => !target.Contains(entry.Key))
            .Select(entry => new PendingChange(entry.Key, entry.Value, DistanceSquared(entry.Key, center)))
            .OrderByDescending(change => change.DistanceSquared)
            .ThenBy(change => change.Cell.Z)
            .ThenBy(change => change.Cell.Y)
            .ThenBy(change => change.Cell.X)
            .ToArray();

        var resultStart = changes.Count;
        foreach (var change in entering)
        {
            var generation = NextGeneration();
            _desired.Add(change.Cell, generation);
            changes.Add(new PartitionChange(
                PartitionChangeKind.Enter,
                new PartitionTicket(change.Cell, generation),
                change.DistanceSquared));
        }

        foreach (var change in exiting)
        {
            _desired.Remove(change.Cell);
            changes.Add(new PartitionChange(
                PartitionChangeKind.Exit,
                new PartitionTicket(change.Cell, change.Generation),
                change.DistanceSquared));
        }

        return changes.Count - resultStart;
    }

    /// <summary>检查异步工作持有的 ticket 是否仍代表当前所需分区。</summary>
    public bool IsCurrent(PartitionTicket ticket)
    {
        EnsureOwnerThread();
        return ticket.IsValid && _desired.TryGetValue(ticket.Cell, out var generation) && generation == ticket.Generation;
    }

    /// <summary>稳定输出全部退出意图并使当前所有 ticket 失效。</summary>
    public int Clear(List<PartitionChange> changes)
    {
        EnsureOwnerThread();
        ArgumentNullException.ThrowIfNull(changes);

        var resultStart = changes.Count;
        foreach (var change in _desired
                     .Select(entry => new PendingChange(entry.Key, entry.Value, 0))
                     .OrderBy(change => change.Cell.Z)
                     .ThenBy(change => change.Cell.Y)
                     .ThenBy(change => change.Cell.X))
        {
            changes.Add(new PartitionChange(
                PartitionChangeKind.Exit,
                new PartitionTicket(change.Cell, change.Generation),
                change.DistanceSquared));
        }

        _desired.Clear();
        return changes.Count - resultStart;
    }

    private readonly record struct PendingChange(PartitionCell Cell, long Generation, long DistanceSquared);

    private HashSet<PartitionCell> BuildTarget(PartitionCell center, PartitionExtent extent)
    {
        var targetCount = CalculateTargetCellCount(extent);
        var minimum = new PartitionCell(
            SubtractExtent(center.X, extent.X),
            SubtractExtent(center.Y, extent.Y),
            SubtractExtent(center.Z, extent.Z));
        var maximum = new PartitionCell(
            AddExtent(center.X, extent.X),
            AddExtent(center.Y, extent.Y),
            AddExtent(center.Z, extent.Z));
        var target = new HashSet<PartitionCell>(checked((int)targetCount));
        for (var z = minimum.Z; ; z++)
        {
            for (var y = minimum.Y; ; y++)
            {
                for (var x = minimum.X; ; x++)
                {
                    target.Add(new PartitionCell(x, y, z));
                    if (x == maximum.X)
                    {
                        break;
                    }
                }

                if (y == maximum.Y)
                {
                    break;
                }
            }

            if (z == maximum.Z)
            {
                break;
            }
        }

        return target;
    }

    private static long CalculateTargetCellCount(PartitionExtent extent)
    {
        try
        {
            var xCount = checked(2L * extent.X + 1L);
            var yCount = checked(2L * extent.Y + 1L);
            var zCount = checked(2L * extent.Z + 1L);
            var total = checked(xCount * yCount * zCount);
            if (total > MaxTargetCellCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(extent),
                    $"目标分区数量 {total} 超过第一版上限 {MaxTargetCellCount}");
            }

            return total;
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(extent), "目标分区数量超出可检查范围");
        }
    }

    private long NextGeneration()
    {
        if (_nextGeneration == long.MaxValue)
        {
            throw new InvalidOperationException("WorldPartition generation 已耗尽");
        }

        return ++_nextGeneration;
    }

    private static long DistanceSquared(PartitionCell cell, PartitionCell center)
    {
        var x = (double)cell.X - center.X;
        var y = (double)cell.Y - center.Y;
        var z = (double)cell.Z - center.Z;
        var distance = x * x + y * y + z * z;
        return distance >= long.MaxValue ? long.MaxValue : (long)distance;
    }

    private static long SubtractExtent(long value, int extent)
    {
        try
        {
            return checked(value - (long)extent);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(extent), "分区范围超出可表示的坐标范围");
        }
    }

    private static long AddExtent(long value, int extent)
    {
        try
        {
            return checked(value + (long)extent);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(extent), "分区范围超出可表示的坐标范围");
        }
    }

    private static long ToCoordinate(float position, float cellSize)
    {
        var coordinate = Math.Floor((double)position / cellSize);
        if (!double.IsFinite(coordinate) || coordinate < long.MinValue || coordinate > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "世界坐标超出可表示的分区坐标范围");
        }

        return (long)coordinate;
    }

    private static float ToFiniteFloat(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < -float.MaxValue || value > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName, "分区边界必须可表示为有限单精度数值");
        }

        return (float)value;
    }

    private static void ValidateCellSize(in Vector3 cellSize)
    {
        if (!float.IsFinite(cellSize.X) || !float.IsFinite(cellSize.Y) || !float.IsFinite(cellSize.Z) ||
            cellSize.X <= 0f || cellSize.Y <= 0f || cellSize.Z <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), "分区尺寸的每个轴都必须是正的有限数值");
        }
    }

    private static void ValidateExtent(PartitionExtent extent)
    {
        if (extent.X < 0 || extent.Y < 0 || extent.Z < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extent), "分区半径不能为负数");
        }
    }

    private static void ValidateFinite(in Vector3 value, string parameterName)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            throw new ArgumentException("世界坐标必须是有限数值", parameterName);
        }
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("WorldPartition 第一版只能在创建它的主线程访问");
        }
    }
}
