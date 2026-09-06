using System.Threading;

namespace Cubit.Sample.Platform;

/// <summary>桌面宿主的帧边界关闭票据；UI 只请求，宿主在安全点实际关闭窗口。</summary>
public sealed class FrameCloseRequest
{
    private int _requested;

    public bool IsRequested => Volatile.Read(ref _requested) != 0;

    public void Request() => Interlocked.Exchange(ref _requested, 1);

    public bool TryConsume() => Interlocked.Exchange(ref _requested, 0) != 0;
}
