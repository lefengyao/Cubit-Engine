using System.Collections.Concurrent;

namespace Cubit.Mcp;

/// <summary>
/// 连接 MCP 传输线程与引擎所有者线程的有界同步队列。
/// 传输线程只负责排队和等待；SceneTree、GPU、音频和脚本访问始终由所有者线程在 <see cref="Pump"/> 中执行。
/// </summary>
public sealed class MainThreadMcpDispatcher : IMcpRequestDispatcher, IDisposable
{
    private readonly ConcurrentQueue<PendingRequest> _pending = new();
    private readonly TimeSpan _requestTimeout;
    private readonly int _maximumPendingRequests;
    private readonly int _ownerThreadId;
    private int _pendingCount;
    private int _disposed;

    public MainThreadMcpDispatcher(
        TimeSpan? requestTimeout = null,
        int maximumPendingRequests = 128)
    {
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5);
        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "MCP 请求超时必须大于零");
        }

        if (maximumPendingRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPendingRequests), "MCP 等待队列上限必须大于零");
        }

        _maximumPendingRequests = maximumPendingRequests;
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>创建此调度器的引擎所有者线程。</summary>
    public int OwnerThreadId => _ownerThreadId;

    /// <summary>当前排队等待主线程处理的请求数量。</summary>
    public int PendingCount => Volatile.Read(ref _pendingCount);

    /// <summary>当前调用是否已经在所有者线程。</summary>
    public bool IsOwnerThread => Environment.CurrentManagedThreadId == _ownerThreadId;

    /// <inheritdoc />
    public string? DispatchMcpRequest(Func<string?> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        if (IsOwnerThread)
        {
            return request();
        }

        if (Interlocked.Increment(ref _pendingCount) > _maximumPendingRequests)
        {
            Interlocked.Decrement(ref _pendingCount);
            throw new InvalidOperationException("MCP 主线程等待队列已满");
        }

        var pending = new PendingRequest(request);
        _pending.Enqueue(pending);
        try
        {
            return pending.Completion.Task.WaitAsync(_requestTimeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            pending.Completion.TrySetException(new TimeoutException("MCP 请求等待引擎主线程超时"));
            throw;
        }
    }

    /// <summary>
    /// 由引擎帧循环在所有者线程调用。返回本帧实际取出的请求数量。
    /// </summary>
    public int Pump(int maximumRequests = 32)
    {
        ThrowIfDisposed();
        if (!IsOwnerThread)
        {
            throw new InvalidOperationException("MCP 主线程调度器只能由创建线程 Pump");
        }

        if (maximumRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRequests), "每帧 MCP 请求上限必须大于零");
        }

        var processed = 0;
        while (processed < maximumRequests && _pending.TryDequeue(out var pending))
        {
            Interlocked.Decrement(ref _pendingCount);
            processed++;
            if (pending.Completion.Task.IsCompleted)
            {
                continue;
            }

            try
            {
                pending.Completion.TrySetResult(pending.Request());
            }
            catch (Exception exception)
            {
                pending.Completion.TrySetException(exception);
            }
        }

        return processed;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        while (_pending.TryDequeue(out var pending))
        {
            Interlocked.Decrement(ref _pendingCount);
            pending.Completion.TrySetException(new ObjectDisposedException(nameof(MainThreadMcpDispatcher)));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private sealed class PendingRequest
    {
        public PendingRequest(Func<string?> request)
        {
            Request = request;
            Completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Func<string?> Request { get; }

        public TaskCompletionSource<string?> Completion { get; }
    }
}
