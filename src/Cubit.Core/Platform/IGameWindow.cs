using Silk.NET.Vulkan;

namespace Cubit.Core.Platform;

/// <summary>
/// 平台窗口抽象。桌面（GLFW）与移动端（SDL/Android）各自实现，
/// 引擎核心只依赖本接口，不依赖任何窗口后端。
/// </summary>
public interface IGameWindow
{
    /// <summary>当前帧缓冲尺寸（像素）。</summary>
    (int Width, int Height) FramebufferSize { get; }

    /// <summary>运行期间底层图形表面是否可能被系统重建（Android SurfaceView 重建）。为 true 时，交换链重建前应重新创建 Vulkan 表面。</summary>
    bool SurfaceMayChange { get; }

    /// <summary>Vulkan 实例需要启用的平台扩展。</summary>
    string[] RequiredVulkanExtensions { get; }

    /// <summary>创建 Vulkan 表面。</summary>
    unsafe SurfaceKHR CreateVulkanSurface(Vk vk, Instance instance);

    /// <summary>窗口初始化完成（Load）。</summary>
    event Action? Load;

    /// <summary>窗口尺寸变化。</summary>
    event Action<int, int>? Resize;

    /// <summary>渲染回调（每秒多次）。</summary>
    event Action<double>? Render;

    /// <summary>启动主循环（阻塞）。</summary>
    void Run();

    /// <summary>请求关闭。</summary>
    void Close();
}

