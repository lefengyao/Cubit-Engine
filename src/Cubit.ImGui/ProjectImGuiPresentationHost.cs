using System.Reflection;
using Cubit.Core.Project;
using Cubit.Core.Rendering;
using Hexa.NET.ImGui;
using Silk.NET.Vulkan;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Cubit.ImGui.Rendering;

/// <summary>项目提供的桌面 ImGui 呈现；仅描述通用输入、帧更新和资源释放。</summary>
public interface IImGuiProjectPresentation : IDisposable
{
    bool UsesPointer { get; }

    void OnMouseMove(float x, float y);

    void OnMouseButton(int button, bool down);

    void OnMouseWheel(float y);

    void OnKey(int key, bool down);

    void OnTextInput(char character);

    void Render(double delta);
}

/// <summary>项目桌面呈现实例化时可用的通用运行时与渲染服务。</summary>
public sealed class ImGuiProjectPresentationContext
{
    public required ProjectRuntime Runtime { get; init; }

    public required ImGuiRenderer Renderer { get; init; }

    public required Action RequestClose { get; init; }
}

/// <summary>
/// 桌面宿主的通用 ImGui 驱动器。它只按项目清单反射加载已静态链接的呈现程序集，
/// 不引用或解释任何项目、体素或玩法类型。
/// </summary>
public sealed class ImGuiProjectPresentationHost : IDisposable
{
    private readonly VulkanContext _vulkan;
    private readonly ImGuiRenderer _renderer;
    private readonly IImGuiProjectPresentation _presentation;
    private readonly Action<CommandBuffer, Framebuffer, Extent2D, Format, uint> _draw;
    private bool _disposed;

    private ImGuiProjectPresentationHost(
        VulkanContext vulkan,
        ImGuiRenderer renderer,
        IImGuiProjectPresentation presentation)
    {
        _vulkan = vulkan;
        _renderer = renderer;
        _presentation = presentation;
        _draw = (commandBuffer, framebuffer, extent, _, frameSlot) =>
        {
            _renderer.DrawCubeMaps(commandBuffer, framebuffer, extent);
            _renderer.Draw(commandBuffer, framebuffer, extent, frameSlot);
        };
        _vulkan.Overlay += _draw;
    }

    public bool UsesPointer => !_disposed && _presentation.UsesPointer;

    public static ImGuiProjectPresentationHost Create(
        VulkanContext vulkan,
        ProjectRuntime runtime,
        Action requestClose)
    {
        ArgumentNullException.ThrowIfNull(vulkan);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(requestClose);

        var reference = runtime.Project.Manifest.DesktopPresentation
            ?? throw new InvalidOperationException("项目没有声明 desktopPresentation 入口");
        var renderer = new ImGuiRenderer(vulkan);
        try
        {
            var presentation = CreatePresentation(reference, new ImGuiProjectPresentationContext
            {
                Runtime = runtime,
                Renderer = renderer,
                RequestClose = requestClose,
            });
            return new ImGuiProjectPresentationHost(vulkan, renderer, presentation);
        }
        catch
        {
            renderer.Dispose();
            throw;
        }
    }

    public void Update(double delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _renderer.NewFrame(ImGuiApi.GetIO(), delta);
        _presentation.Render(delta);
        _renderer.Render();
    }

    public void OnMouseMove(float x, float y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _presentation.OnMouseMove(x, y);
    }

    public void OnMouseButton(int button, bool down)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _presentation.OnMouseButton(button, down);
    }

    public void OnMouseWheel(float y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _presentation.OnMouseWheel(y);
    }

    public void OnKey(int key, bool down)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _presentation.OnKey(key, down);
    }

    public void OnTextInput(char character)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _presentation.OnTextInput(character);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _vulkan.Overlay -= _draw;
        try
        {
            _presentation.Dispose();
        }
        finally
        {
            _renderer.Dispose();
        }
    }

    private static IImGuiProjectPresentation CreatePresentation(
        CubitProjectDesktopPresentationReference reference,
        ImGuiProjectPresentationContext context)
    {
        Assembly assembly;
        try
        {
            assembly = Assembly.Load(new AssemblyName(reference.Assembly));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"无法加载项目 desktopPresentation 程序集: {reference.Assembly}", exception);
        }

        var type = assembly.GetType(reference.Type, throwOnError: false, ignoreCase: false);
        if (type is null)
        {
            throw new InvalidOperationException($"项目 desktopPresentation 类型不存在: {reference.Type}");
        }

        if (!type.IsPublic || type.IsAbstract || type.IsInterface ||
            !typeof(IImGuiProjectPresentation).IsAssignableFrom(type))
        {
            throw new InvalidOperationException(
                $"项目 desktopPresentation 类型必须是公开具体的 {nameof(IImGuiProjectPresentation)}: {reference.Type}");
        }

        var constructor = type.GetConstructor([typeof(ImGuiProjectPresentationContext)])
            ?? throw new InvalidOperationException(
                $"项目 desktopPresentation 类型必须提供公共 {nameof(ImGuiProjectPresentationContext)} 构造: {reference.Type}");
        try
        {
            return (IImGuiProjectPresentation)(constructor.Invoke([context])
                ?? throw new InvalidOperationException($"创建项目 desktopPresentation 返回空值: {reference.Type}"));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"创建项目 desktopPresentation 失败: {reference.Type}", exception);
        }
    }
}
