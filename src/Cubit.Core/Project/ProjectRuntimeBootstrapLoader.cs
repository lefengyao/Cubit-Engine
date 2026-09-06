using System.Reflection;
using Cubit.Core.Jobs;
using Cubit.Core.Plugins;
using Cubit.Core.Rendering;
using Cubit.Core.Scene;

namespace Cubit.Core.Project;

/// <summary>从项目清单的显式 runtime 声明构造 Scene-First 运行会话。</summary>
public sealed class ProjectRuntimeBootstrapSession : IDisposable
{
    private bool _disposed;

    internal ProjectRuntimeBootstrapSession(
        ProjectRuntime runtime,
        IProjectRuntimeBootstrap bootstrap,
        IProjectInputRouter? inputRouter)
    {
        Runtime = runtime;
        Bootstrap = bootstrap;
        InputRouter = inputRouter;
    }

    public ProjectRuntime Runtime { get; }

    public IProjectRuntimeBootstrap Bootstrap { get; }

    public IProjectInputRouter? InputRouter { get; }

    public void RouteInput(ProjectInputEvent inputEvent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        InputRouter?.Handle(inputEvent);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Runtime.Dispose();
    }
}

/// <summary>仅加载清单明确声明且已由宿主静态链接的项目 bootstrap。</summary>
public static class ProjectRuntimeBootstrapLoader
{
    public static ProjectRuntimeBootstrapSession Create(
        CubitProject project,
        IRenderBackend renderer,
        Action<EngineContext>? configureHostServices = null,
        JobSystemOptions? jobSystemOptions = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(renderer);

        var bootstrap = CreateBootstrap(project.Manifest.Runtime);
        IProjectInputRouter? inputRouter = null;
        ProjectRuntime? runtime = null;
        try
        {
            runtime = new ProjectRuntime(
                project,
                renderer,
                bootstrap.CreateAvailablePlugins(),
                bootstrap.RegisterProjectTypes,
                configureHostServices,
                bootstrap.ConfigureLoadedScene,
                jobSystemOptions,
                context =>
                {
                    bootstrap.ConfigureRuntime(context);
                    inputRouter = bootstrap.CreateInputRouter(context);
                });
            return new ProjectRuntimeBootstrapSession(runtime, bootstrap, inputRouter);
        }
        catch
        {
            runtime?.Dispose();
            throw;
        }
    }

    private static IProjectRuntimeBootstrap CreateBootstrap(CubitProjectRuntimeReference? reference)
    {
        if (reference is null)
        {
            throw new InvalidOperationException("项目没有声明 runtime 引导器");
        }

        Assembly assembly;
        try
        {
            assembly = Assembly.Load(new AssemblyName(reference.Assembly));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"无法加载项目 runtime 程序集: {reference.Assembly}", exception);
        }

        var type = assembly.GetType(reference.Type, throwOnError: false, ignoreCase: false);
        if (type is null)
        {
            throw new InvalidOperationException($"项目 runtime 类型不存在: {reference.Type} ({reference.Assembly})");
        }

        if (!type.IsPublic || type.IsAbstract || type.IsInterface ||
            !typeof(IProjectRuntimeBootstrap).IsAssignableFrom(type))
        {
            throw new InvalidOperationException(
                $"项目 runtime 类型必须是公开具体的 {nameof(IProjectRuntimeBootstrap)}: {reference.Type}");
        }

        var constructor = type.GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException($"项目 runtime 类型必须提供公共无参构造: {reference.Type}");
        try
        {
            return (IProjectRuntimeBootstrap)(constructor.Invoke(null)
                ?? throw new InvalidOperationException($"创建项目 runtime 引导器返回空值: {reference.Type}"));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"创建项目 runtime 引导器失败: {reference.Type}", exception);
        }
    }
}
