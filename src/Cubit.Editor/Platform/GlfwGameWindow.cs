using System.Runtime.InteropServices;
using Cubit.Core.Platform;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

namespace Cubit.Editor.Platform;

/// <summary>编辑器桌面 GLFW 窗口适配，不依赖任何项目或示例程序集。</summary>
internal sealed class GlfwGameWindow : IGameWindow
{
    private readonly IView _view;
    private IVkSurface? _vkSurface;

    private IVkSurface VkSurface =>
        _vkSurface ??= ((IVkSurfaceSource)_view).VkSurface
            ?? throw new InvalidOperationException("当前窗口后端不支持 Vulkan 表面");

    public GlfwGameWindow(IView view)
    {
        _view = view;
        _view.Load += () => Load?.Invoke();
        _view.Resize += size => Resize?.Invoke(size.X, size.Y);
        _view.Render += delta => Render?.Invoke(delta);
    }

    public (int Width, int Height) FramebufferSize => (_view.FramebufferSize.X, _view.FramebufferSize.Y);

    public bool SurfaceMayChange => false;

    public unsafe string[] RequiredVulkanExtensions
    {
        get
        {
            var names = VkSurface.GetRequiredExtensions(out var count);
            var result = new string[count];
            for (var index = 0; index < count; index++)
            {
                result[index] = Marshal.PtrToStringAnsi((nint)names[index])!;
            }

            return result;
        }
    }

    public unsafe SurfaceKHR CreateVulkanSurface(Vk vk, Instance instance)
    {
        var handle = VkSurface.Create<Win32SurfaceCreateInfoKHR>(new VkHandle(instance.Handle), null);
        return new SurfaceKHR { Handle = handle.Handle };
    }

    public event Action? Load;

    public event Action<int, int>? Resize;

    public event Action<double>? Render;

    public void Run() => _view.Run();

    public void Close() => _view.Close();
}
