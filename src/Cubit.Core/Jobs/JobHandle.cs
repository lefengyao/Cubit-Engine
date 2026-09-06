namespace Cubit.Core.Jobs;

public sealed class JobHandle
{
    private readonly JobGroup _group;

    internal JobHandle(JobGroup group)
    {
        _group = group;
    }

    public bool IsCompleted => _group.IsCompleted;

    public bool IsCancellationRequested => _group.IsCancellationRequested;

    /// <summary>请求取消尚未完成的 Job；任务本身仍需通过 Complete 回收。</summary>
    public void Cancel() => _group.Cancel();

    public void Complete() => _group.Complete();
}

internal sealed class JobGroup
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Collections.Concurrent.ConcurrentQueue<Exception> _errors = new();
    private readonly CancellationTokenSource _cancellation;
    private int _remaining;

    public JobGroup(int count, CancellationToken cancellationToken)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _remaining = count;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (count == 0)
        {
            _completion.TrySetResult();
        }
    }

    public CancellationToken Token => _cancellation.Token;

    public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

    public bool IsCompleted => _completion.Task.IsCompleted;

    public void Cancel() => _cancellation.Cancel();

    public void RecordFailure(string name, Exception exception)
    {
        _errors.Enqueue(new JobExecutionException(name, exception));
        _cancellation.Cancel();
    }

    public void CompleteOne()
    {
        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            _completion.TrySetResult();
        }
    }

    public void Complete()
    {
        _completion.Task.GetAwaiter().GetResult();
        var errors = _errors.ToArray();
        if (errors.Length > 0)
        {
            throw new AggregateException("一个或多个 Job 执行失败", errors);
        }

        if (_cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException(_cancellation.Token);
        }
    }
}
