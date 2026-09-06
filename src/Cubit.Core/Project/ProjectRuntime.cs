using Cubit.Core.Jobs;
using Cubit.Core.Plugins;
using Cubit.Core.Rendering;
using Cubit.Core.Scene;

namespace Cubit.Core.Project;

/// <summary>
/// 平台无关的项目运行会话：插件先注册类型，随后只从项目主场景构造运行树。
/// 平台宿主只负责提供窗口、输入和可选设备服务，不能替代项目创建作者节点。
/// </summary>
public sealed class ProjectRuntime : IDisposable
{
    private readonly PluginManager _pluginManager;
    private readonly Action<CubitProject, SceneRegistry>? _registerProjectTypes;
    private readonly Action<EngineContext>? _configureHostServices;
    private readonly Action<Node, EngineContext>? _configureScene;
    private readonly Action<EngineContext>? _configureProjectRuntime;
    private bool _disposed;

    public ProjectRuntime(
        CubitProject project,
        IRenderBackend renderer,
        IEnumerable<IEnginePlugin> availablePlugins,
        Action<CubitProject, SceneRegistry>? registerProjectTypes = null,
        Action<EngineContext>? configureHostServices = null,
        Action<Node, EngineContext>? configureScene = null,
        JobSystemOptions? jobSystemOptions = null,
        Action<EngineContext>? configureProjectRuntime = null)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(availablePlugins);

        Tree = new SceneTree();
        RenderingServer = new RenderingServer(renderer);
        Jobs = new JobSystem(jobSystemOptions);
        SceneRegistry = new SceneRegistry();
        _pluginManager = new PluginManager(availablePlugins, SceneRegistry);
        _registerProjectTypes = registerProjectTypes;
        _configureHostServices = configureHostServices;
        _configureScene = configureScene;
        _configureProjectRuntime = configureProjectRuntime;

        try
        {
            _pluginManager.Configure(Project.Manifest.Plugins);
            _registerProjectTypes?.Invoke(Project, SceneRegistry);
            Context = new EngineContext()
                .AddService(RenderingServer)
                .AddService(Jobs)
                .AddService(Project);
            _configureHostServices?.Invoke(Context);
            _pluginManager.Start(Context);
            Scenes = new RuntimeSceneService(
                Project,
                Tree,
                SceneRegistry,
                root =>
                {
                    InjectServices(root);
                    _configureScene?.Invoke(root, Context);
                });
            Context.AddService(Scenes);
            _configureProjectRuntime?.Invoke(Context);
            ReloadMainScene();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public CubitProject Project { get; }

    public SceneTree Tree { get; }

    public RenderingServer RenderingServer { get; }

    public JobSystem Jobs { get; }

    public EngineContext Context { get; }

    public PluginManager Plugins => _pluginManager;

    public SceneRegistry SceneRegistry { get; }

    public RuntimeSceneService Scenes { get; }

    /// <summary>重新读取项目清单指定的主场景；不会回退到宿主硬编码的默认节点。</summary>
    public void ReloadMainScene()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            Scenes.Switch(Project.SceneCatalog.MainScene.Id);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"加载项目主场景失败: {Project.MainScenePath}", exception);
        }
    }

    /// <summary>用已反序列化的场景根替换运行树；供项目内场景切换复用统一服务注入。</summary>
    public void ReplaceScene(Node root)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(root);
        Scenes.Replace(root);
    }

    /// <summary>推进一次项目逻辑并提交主场景中第一个 Camera3D 的渲染视图。</summary>
    public void ProcessFrame(double delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RenderingServer.BeginFrame();
        Tree.ProcessFrame(delta);

        var camera = FindFirst<Camera3D>(Tree.Root);
        if (camera is null)
        {
            return;
        }

        var (width, height) = RenderingServer.FramebufferSize;
        var aspect = height > 0 ? width / (float)height : 1f;
        RenderingServer.UpdateCamera(
            camera.GetViewMatrix((float)Tree.PhysicsInterpolationAlpha),
            camera.ProjectionMatrix(aspect));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Exception>? errors = null;

        try
        {
            Tree.Dispose();
        }
        catch (Exception exception)
        {
            (errors ??= []).Add(exception);
        }

        try
        {
            _pluginManager.Dispose();
        }
        catch (Exception exception)
        {
            (errors ??= []).Add(exception);
        }

        try
        {
            Jobs.Dispose();
        }
        catch (Exception exception)
        {
            (errors ??= []).Add(exception);
        }

        try
        {
            RenderingServer.Dispose();
        }
        catch (Exception exception)
        {
            (errors ??= []).Add(exception);
        }

        if (errors is { Count: > 0 })
        {
            throw new AggregateException("项目运行时释放不完整", errors);
        }
    }

    private void InjectServices(Node node)
    {
        if (node is IRenderingServerConsumer renderingConsumer)
        {
            renderingConsumer.Server = RenderingServer;
        }

        if (node is IJobSystemConsumer jobsConsumer)
        {
            jobsConsumer.Jobs = Jobs;
        }

        if (node is IEngineContextConsumer contextConsumer)
        {
            contextConsumer.SetEngineContext(Context);
        }

        foreach (var child in node.Children)
        {
            InjectServices(child);
        }
    }

    private static T? FindFirst<T>(Node node) where T : Node
    {
        if (node is T typed)
        {
            return typed;
        }

        foreach (var child in node.Children)
        {
            var found = FindFirst<T>(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
