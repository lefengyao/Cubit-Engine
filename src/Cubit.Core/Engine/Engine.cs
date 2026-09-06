using System.Diagnostics;
using System.Threading;
using Cubit.Core.Platform;
using Cubit.Core.Rendering;

namespace Cubit.Core.Engine;

/// <summary>引擎入口：把平台窗口与 Vulkan 渲染连接起来。</summary>
public sealed class Engine : IDisposable
{
    // 平台回调可能在前台恢复、场景加载或驱动阻塞后返回异常长时长；不让单次更新跨越多个游戏帧。
    private const double MaximumFrameDeltaSeconds = 0.25d;

    private readonly IGameWindow _window;
    private readonly TaskCompletionSource<bool> _loadCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private VulkanContext? _vulkan;
    private Thread? _renderThread;
    private volatile bool _closing;
    private volatile bool _loadSucceeded;
    private int _platformCloseRequested;
    private int _renderCallbackDepth;
    private int _runStarted;
    private int _disposed;
    private long _lastErrorLogTicks;

    public Engine(IGameWindow window)
    {
        _window = window;
    }

    /// <summary>引擎显示设置（逻辑窗口尺寸 + 拉伸策略），桌面与移动端一致。</summary>
    public DisplaySettings Display { get; } = new();

    /// <summary>
    /// 自驱渲染：引擎用专用线程按固定节奏驱动 Update/Render，
    /// 不依赖窗口后端 Render 回调（Android SDL 后台恢复后不再回调时的规避方案）。
    /// </summary>
    public bool SelfDriven { get; set; }

    /// <summary>配置显示：逻辑窗口尺寸决定渲染分辨率；keepAspect=true 时自动 letterbox 防变形。</summary>
    public void ConfigureDisplay(int logicalWidth, int logicalHeight, bool keepAspect = true)
    {
        Display.LogicalWidth = logicalWidth;
        Display.LogicalHeight = logicalHeight;
        Display.KeepAspect = keepAspect;
    }

    /// <summary>Vulkan 上下文（Load 后可用）。</summary>
    public VulkanContext? Vulkan => _vulkan;

    /// <summary>窗口初始化完成（Load）后触发；此时可安全创建游戏。</summary>
    public event Action? Load;

    /// <summary>每帧更新回调（渲染前调用）。</summary>
    public event Action<double>? Update;

    /// <summary>从任意平台线程请求关闭窗口；渲染回调内的请求会避开当前帧的 Vulkan 提交。</summary>
    public void RequestClose()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _closing = true;
        _loadCompleted.TrySetResult(true);
        if (Volatile.Read(ref _renderCallbackDepth) == 0)
        {
            RequestPlatformClose();
        }
    }

    /// <summary>平台宿主可订阅的渲染异常；不改变引擎的异常隔离策略。</summary>
    public event Action<Exception>? RenderError;

    public void Run()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(Engine));
        }

        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException("Engine.Run 只能调用一次");
        }

        _window.Load += OnWindowLoad;

        if (SelfDriven)
        {
            // 自驱渲染：后台线程按约 60FPS 调用 Update + RenderFrame；
            // 表面不可用（后台/恢复中）时放慢重试，恢复后自动重建继续渲染。
            _renderThread = new Thread(RenderLoopSelfDriven)
            {
                IsBackground = true,
                Name = "CubitRender",
            };
            _renderThread.Start();
        }
        else
        {
            _window.Render += delta =>
            {
                Interlocked.Increment(ref _renderCallbackDepth);
                try
                {
                    if (_closing)
                    {
                        return;
                    }

                    var frameDelta = NormalizeFrameDelta(delta);
                    Update?.Invoke(frameDelta);
                    if (_closing)
                    {
                        return;
                    }

                    _vulkan?.RenderFrame();
                }
                catch (Exception ex)
                {
                    LogRenderError(ex);
                }
                finally
                {
                    if (Interlocked.Decrement(ref _renderCallbackDepth) == 0 && _closing)
                    {
                        RequestPlatformClose();
                    }
                }
            };
        }

        // 窗口主循环（事件/输入/生命周期）；自驱模式下仅用于事件，渲染由专用线程完成。
        try
        {
            _window.Run();
        }
        finally
        {
            if (SelfDriven)
            {
                _closing = true;
                _loadCompleted.TrySetResult(true);
                JoinRenderThread();
            }
        }
    }

    private void OnWindowLoad()
    {
        try
        {
            if (_closing)
            {
                return;
            }

            _vulkan = new VulkanContext(_window, Display);
            Load?.Invoke();
            _loadSucceeded = true;
        }
        finally
        {
            // 即使 Vulkan 或游戏 Load 失败，也必须解除自驱线程的等待，避免后台线程泄漏。
            _loadCompleted.TrySetResult(true);
        }
    }

    private void RenderLoopSelfDriven()
    {
        try
        {
            _loadCompleted.Task.GetAwaiter().GetResult();
            if (!_loadSucceeded || _closing)
            {
                return;
            }

            var last = Stopwatch.GetTimestamp();
            while (!_closing)
            {
                var now = Stopwatch.GetTimestamp();
                var delta = (double)(now - last) / Stopwatch.Frequency;
                last = now;
                Interlocked.Increment(ref _renderCallbackDepth);
                try
                {
                    var frameDelta = NormalizeFrameDelta(delta);
                    Update?.Invoke(frameDelta);
                    if (!_closing)
                    {
                        _vulkan?.RenderFrame();
                    }
                }
                catch (Exception ex)
                {
                    LogRenderError(ex);
                    // 表面不可用（后台/恢复中）：放慢重试，避免刷屏与忙等
                    Thread.Sleep(200);
                    last = Stopwatch.GetTimestamp();
                    continue;
                }
                finally
                {
                    if (Interlocked.Decrement(ref _renderCallbackDepth) == 0 && _closing)
                    {
                        RequestPlatformClose();
                    }
                }

                if (_closing)
                {
                    break;
                }

                var elapsedMs = (Stopwatch.GetTimestamp() - now) * 1000.0 / Stopwatch.Frequency;
                var sleepMs = 16 - (int)elapsedMs;
                if (sleepMs > 0)
                {
                    Thread.Sleep(sleepMs);
                }
            }
        }
        finally
        {
            // Dispose 可能由 Update 回调所在的渲染线程发起，此时不能自连接；由线程自己释放 Vulkan。
            if (ReferenceEquals(Thread.CurrentThread, _renderThread))
            {
                DisposeVulkan();
            }
        }
    }

    /// <summary>将不可信的平台帧时长限制为单帧可安全处理的范围。</summary>
    private static double NormalizeFrameDelta(double delta)
    {
        if (!double.IsFinite(delta) || delta <= 0d)
        {
            return 0d;
        }

        return Math.Min(delta, MaximumFrameDeltaSeconds);
    }

    private void LogRenderError(Exception ex)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastErrorLogTicks) < 1000)
        {
            return;
        }
        Interlocked.Exchange(ref _lastErrorLogTicks, now);
        Console.Error.WriteLine($"[Cubit] 渲染异常: {ex.Message}");
        RenderError?.Invoke(ex);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _closing = true;
        _loadCompleted.TrySetResult(true);
        var calledFromRenderThread = ReferenceEquals(Thread.CurrentThread, _renderThread);
        if (!calledFromRenderThread)
        {
            JoinRenderThread();
            DisposeVulkan();
        }
    }

    private void JoinRenderThread()
    {
        var renderThread = _renderThread;
        if (renderThread is null || ReferenceEquals(Thread.CurrentThread, renderThread))
        {
            return;
        }

        renderThread.Join();
    }

    private void DisposeVulkan()
    {
        var vulkan = Interlocked.Exchange(ref _vulkan, null);
        vulkan?.Dispose();
    }

    /// <summary>平台 Close 只能发出一次，避免多个关闭来源在窗口销毁期间重入。</summary>
    private void RequestPlatformClose()
    {
        if (Interlocked.Exchange(ref _platformCloseRequested, 1) == 0)
        {
            _window.Close();
        }
    }
}
