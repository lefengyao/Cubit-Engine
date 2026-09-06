namespace Cubit.Core.Jobs;

using System.Collections.Concurrent;

public sealed class JobSystem : IDisposable
{
    private sealed record WorkItem(
        string Name,
        Action<CancellationToken> Action,
        JobGroup Group);

    private readonly ConcurrentQueue<WorkItem>[] _queues;
    private readonly Thread[] _workers;
    private readonly SemaphoreSlim _available = new(0, int.MaxValue);
    private readonly ManualResetEventSlim _drained = new(initialState: true);
    private readonly object _submissionLock = new();
    private bool _accepting = true;
    private volatile bool _shutdown;
    private int _nextQueue = -1;
    private int _pending;
    private bool _disposed;

    public JobSystem(JobSystemOptions? options = null)
    {
        var workerCount = (options ?? new JobSystemOptions()).WorkerCount;
        if (workerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "worker 数必须大于 0");
        }

        WorkerCount = workerCount;
        _queues = Enumerable.Range(0, workerCount)
            .Select(_ => new ConcurrentQueue<WorkItem>())
            .ToArray();
        _workers = new Thread[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            var workerIndex = i;
            _workers[i] = new Thread(() => WorkerLoop(workerIndex))
            {
                IsBackground = true,
                Name = $"Cubit-Job-{workerIndex}",
            };
            _workers[i].Start();
        }
    }

    public int WorkerCount { get; }

    public JobHandle Schedule(
        Action<CancellationToken> action,
        string name = "job",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var normalizedName = NormalizeName(name);
        var group = new JobGroup(1, cancellationToken);
        Submit([new WorkItem(normalizedName, action, group)]);
        return new JobHandle(group);
    }

    public JobHandle ScheduleBatch(
        IEnumerable<Action<CancellationToken>> actions,
        string name = "batch",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var normalizedName = NormalizeName(name);
        var actionArray = actions.ToArray();
        if (actionArray.Any(action => action is null))
        {
            throw new ArgumentException("Job 批次不能包含空操作", nameof(actions));
        }

        var group = new JobGroup(actionArray.Length, cancellationToken);
        var items = new WorkItem[actionArray.Length];
        for (var i = 0; i < actionArray.Length; i++)
        {
            items[i] = new WorkItem($"{normalizedName}[{i}]", actionArray[i], group);
        }

        Submit(items);
        return new JobHandle(group);
    }

    public JobHandle ParallelFor(
        int fromInclusive,
        int toExclusive,
        int batchSize,
        Action<int, int, CancellationToken> rangeAction,
        string name = "parallel-for",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rangeAction);
        if (fromInclusive > toExclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(fromInclusive), "起点不能大于终点");
        }

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "分片大小必须大于 0");
        }

        var actions = new List<Action<CancellationToken>>();
        for (var start = fromInclusive; start < toExclusive; start += batchSize)
        {
            var rangeStart = start;
            var rangeEnd = Math.Min(start + batchSize, toExclusive);
            actions.Add(token => rangeAction(rangeStart, rangeEnd, token));
        }

        return ScheduleBatch(actions, name, cancellationToken);
    }

    public void Dispose()
    {
        lock (_submissionLock)
        {
            if (_disposed)
            {
                return;
            }

            _accepting = false;
        }

        _drained.Wait();
        _shutdown = true;
        _available.Release(WorkerCount);
        foreach (var worker in _workers)
        {
            worker.Join();
        }

        _available.Dispose();
        _drained.Dispose();
        _disposed = true;
    }

    private void Submit(IReadOnlyList<WorkItem> items)
    {
        lock (_submissionLock)
        {
            ObjectDisposedException.ThrowIf(!_accepting, this);
            if (items.Count == 0)
            {
                return;
            }

            _drained.Reset();
            Interlocked.Add(ref _pending, items.Count);
            foreach (var item in items)
            {
                var queueIndex = (int)((uint)Interlocked.Increment(ref _nextQueue) % (uint)WorkerCount);
                _queues[queueIndex].Enqueue(item);
            }

            _available.Release(items.Count);
        }
    }

    private void WorkerLoop(int workerIndex)
    {
        while (true)
        {
            if (TryTake(workerIndex, out var item))
            {
                Execute(item);
                continue;
            }

            if (_shutdown)
            {
                return;
            }

            _available.Wait();
        }
    }

    private bool TryTake(int workerIndex, out WorkItem item)
    {
        if (_queues[workerIndex].TryDequeue(out item!))
        {
            return true;
        }

        for (var offset = 1; offset < WorkerCount; offset++)
        {
            var victim = (workerIndex + offset) % WorkerCount;
            if (_queues[victim].TryDequeue(out item!))
            {
                return true;
            }
        }

        item = null!;
        return false;
    }

    private void Execute(WorkItem item)
    {
        try
        {
            if (!item.Group.Token.IsCancellationRequested)
            {
                item.Action(item.Group.Token);
            }
        }
        catch (OperationCanceledException) when (item.Group.Token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            item.Group.RecordFailure(item.Name, ex);
        }
        finally
        {
            item.Group.CompleteOne();
            if (Interlocked.Decrement(ref _pending) == 0)
            {
                _drained.Set();
            }
        }
    }

    private static string NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "job" : name.Trim();
}
