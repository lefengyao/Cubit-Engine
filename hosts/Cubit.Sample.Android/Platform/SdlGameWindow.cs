using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

namespace Cubit.Sample.Android.Platform;

/// <summary>Android SDL 视图适配 <see cref="Cubit.Core.Platform.IGameWindow"/>。</summary>
public sealed class SdlGameWindow : Cubit.Core.Platform.IGameWindow
{
    private readonly IView _view;
    private IVkSurface? _vkSurface;

    private IVkSurface VkSurface =>
        _vkSurface ??= ((IVkSurfaceSource)_view).VkSurface
            ?? throw new InvalidOperationException("当前窗口后端不支持 Vulkan 表面");

    public SdlGameWindow(IView view)
    {
        _view = view;
        _view.Load += () => Load?.Invoke();
        _view.Resize += size => Resize?.Invoke(size.X, size.Y);
        _view.Render += delta => Render?.Invoke(delta);
    }

    public (int Width, int Height) FramebufferSize => (_view.FramebufferSize.X, _view.FramebufferSize.Y);

    /// <summary>Android SurfaceView 可能被系统重建，交换链重建前必须同步重建 Vulkan 表面。</summary>
    public bool SurfaceMayChange => true;

    public unsafe string[] RequiredVulkanExtensions
    {
        get
        {
            var names = VkSurface.GetRequiredExtensions(out var count);
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = Marshal.PtrToStringAnsi((nint)names[i])!;
            }
            return result;
        }
    }

    public unsafe SurfaceKHR CreateVulkanSurface(Vk vk, Instance instance)
    {
        var handle = VkSurface.Create<AndroidSurfaceCreateInfoKHR>(new VkHandle(instance.Handle), null);
        return new SurfaceKHR { Handle = handle.Handle };
    }

    public event Action? Load;

    public event Action<int, int>? Resize;

    public event Action<double>? Render;

    public void Run() => _view.Run();

    public void Close() => _view.Close();
}
