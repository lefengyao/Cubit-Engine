using System.Reflection;
using System.Runtime.Loader;
using Cubit.Animation;
using Cubit.Audio;
using Cubit.Content;
using Cubit.Content.Importers;
using Cubit.Core.Jobs;
using Cubit.Core.Plugins;
using Cubit.Core.Project;
using Cubit.Core.Rendering;
using Cubit.Core.Scene;
using Cubit.Physics;
using Cubit.Scripting;
using Cubit.Voxel;

namespace Cubit.Editor.Scenes;

/// <summary>正式 Cubit 项目的编辑器场景会话；编辑态只注册类型，运行服务只属于预览。</summary>
public sealed class ProjectSceneSession : IEditorSceneSession, IEditorPreviewSession
{
    public CubitProject Project { get; }

    public string DisplayName => Project.Manifest.Name;

    public string ScenePath => Project.SceneCatalog.GetById(CurrentSceneId).AbsolutePath;

    public string CurrentSceneId { get; private set; } = "main";

    public SceneTree Tree { get; } = new();

    public RenderingServer RenderingServer { get; }

    /// <summary>当前编辑会话自己的节点类型注册表，不与其他项目或全局 ClassDB 共享。</summary>
    public SceneRegistry SceneRegistry { get; }

    /// <summary>当前项目会话自己的插件注册表。</summary>
    public PluginRegistry PluginRegistry => _pluginManager.Registry;

    /// <summary>当前项目会话自己的项目编辑器扩展 registry，不与官方插件扩展混用。</summary>
    public ProjectEditorRegistry ProjectEditorRegistry { get; } = new();

    private readonly JobSystem _jobs;
    private readonly PluginManager _pluginManager;
    private readonly CubitAudioPlugin _audioPlugin;
    private readonly CubitScriptingPlugin _scriptingPlugin;
    private ContentService? _previewContentService;
    private ScriptRuntime? _previewScriptRuntime;
    private EngineContext? _previewEngineContext;
    private PreviewBootstrapLoadContext? _previewBootstrapLoadContext;
    private WeakReference? _previewBootstrapLoadContextReference;
    private EditorProjectLoadContext? _editorProjectLoadContext;
    private bool _isPreviewing;
    private bool _disposed;

    public bool HasCamera => FindCamera(Tree.Root) is not null;

    public bool CanStartPreview => Project.Manifest.Preview is not null;

    public bool IsPreviewing => _isPreviewing;

    internal AudioServer? PreviewAudioServer => _isPreviewing ? _audioPlugin.Server : null;

    internal ContentService? PreviewContentService => _isPreviewing ? _previewContentService : null;

    internal ScriptRuntime? PreviewScriptRuntime => _isPreviewing ? _previewScriptRuntime : null;

    /// <summary>仅在预览运行期间保留的服务上下文；停止预览后必须立即撤销。</summary>
    internal EngineContext? PreviewEngineContext => _isPreviewing ? _previewEngineContext : null;

    public ProjectSceneSession(IRenderBackend renderer, string projectDirectory)
    {
        RenderingServer = new RenderingServer(renderer);
        _jobs = new JobSystem();
        SceneRegistry = new SceneRegistry();
        Tree.LifecycleEnabled = false;
        _audioPlugin = new CubitAudioPlugin();
        _scriptingPlugin = new CubitScriptingPlugin();
        _pluginManager = new PluginManager([
            new CubitVoxelPlugin(),
            new CubitPhysicsPlugin(),
            new CubitAnimationPlugin(),
            _audioPlugin,
            new CubitContentPlugin(new Cubit.Core.Diagnostics.DiagnosticBag()),
            _scriptingPlugin,
        ], SceneRegistry);
        try
        {
            Project = ProjectIO.Load(projectDirectory);
            CurrentSceneId = Project.SceneCatalog.MainScene.Id;
            // 编辑态只完成清单校验和当前会话 registry 类型注册；绝不启动设备、服务或用户代码。
            _pluginManager.Configure(Project.Manifest.Plugins);
            LoadEditorProjectTypes();
            LoadAuthoringScene();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void ProcessFrame(double delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RenderingServer.BeginFrame();
        Tree.ProcessFrame(delta);
        var camera = FindCamera(Tree.Root);
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

    public void StartPreview()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isPreviewing)
        {
            return;
        }

        try
        {
            var previewRoot = ProjectIO.LoadScene(Project, CurrentSceneId, SceneRegistry);
            _previewContentService = new ContentService([
                new SceneJsonContentImporter(),
                new PngRgba8ContentImporter(),
            ]);
            _previewEngineContext = new EngineContext()
                .AddService(RenderingServer)
                .AddService(_jobs)
                .AddService(_previewContentService);
            _pluginManager.Start(_previewEngineContext);

            ConfigurePreview(previewRoot);
            InjectServices(previewRoot);
            if (_audioPlugin.Server is not null)
            {
                AudioSceneBinding.Attach(previewRoot, _audioPlugin.Server);
            }

            Tree.LifecycleEnabled = true;
            Tree.ReplaceChildren(previewRoot);
            StartPreviewScripts();
            Tree.Paused = false;
            _isPreviewing = true;
        }
        catch (Exception primary)
        {
            try
            {
                CleanupPreview(reloadAuthoringScene: true);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException("项目预览启动失败且回滚不完整", primary, cleanup);
            }

            throw;
        }
    }

    public void StopPreview()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isPreviewing && !_pluginManager.IsStarted)
        {
            return;
        }

        CleanupPreview(reloadAuthoringScene: true);
    }

    public void Save()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isPreviewing)
        {
            throw new InvalidOperationException("运行项目预览时不能保存作者场景；请先停止预览");
        }

        ProjectIO.SaveScene(Project, CurrentSceneId, Tree.Root);
    }

    /// <summary>打开项目目录中的任意作者场景；预览期间禁止切换编辑场景。</summary>
    public void OpenScene(string sceneId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isPreviewing)
        {
            throw new InvalidOperationException("运行项目预览时不能打开作者场景；请先停止预览");
        }

        var root = ProjectIO.LoadScene(Project, sceneId, SceneRegistry);
        InjectServices(root);
        Tree.ReplaceChildren(root);
        CurrentSceneId = Project.SceneCatalog.GetById(sceneId).Id;
        Tree.Paused = true;
    }

    /// <summary>从项目主场景重新装载作者数据；预览运行时禁止替换。</summary>
    internal void ResetAuthoringScene()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isPreviewing)
        {
            throw new InvalidOperationException("运行项目预览时不能重置作者场景；请先停止预览");
        }

        LoadAuthoringScene();
    }

    /// <summary>为编辑态新建节点注入受控的渲染与 Job 服务。</summary>
    internal void InjectEditorServices(Node node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(node);
        InjectServices(node);
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
            CleanupPreview(reloadAuthoringScene: false);
        }
        catch (Exception exception)
        {
            (errors ??= []).Add(exception);
        }

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
            ReleaseEditorProjectLoadContext();
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
            _jobs.Dispose();
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
            throw new AggregateException("编辑器项目会话释放不完整", errors);
        }
    }

    private void StartPreviewScripts()
    {
        var attachments = CollectScriptAttachments(Tree.Root);
        if (attachments.Count == 0)
        {
            return;
        }

        var reference = Project.Manifest.Preview
            ?? throw new InvalidOperationException("声明脚本附件的项目必须提供 preview 入口");
        _previewScriptRuntime = _scriptingPlugin.CreateRuntime(Tree, Project.RootDirectory);
        _previewEngineContext?.AddService(_previewScriptRuntime);
        _previewScriptRuntime.LoadAssembly(reference.Assembly);
        _previewScriptRuntime.Start(attachments);
        Tree.Root.AddChild(new ScriptRuntimeDriver(_previewScriptRuntime));
    }

    private static List<ScriptAttachment> CollectScriptAttachments(Node node)
    {
        var attachments = new List<ScriptAttachment>();
        CollectScriptAttachmentsRecursive(node, attachments);
        return attachments;
    }

    private static void CollectScriptAttachmentsRecursive(Node node, List<ScriptAttachment> attachments)
    {
        if (node is ScriptAttachmentNode attachmentNode)
        {
            attachments.Add(attachmentNode.Attachment ?? throw new ScriptRuntimeException(
                "脚本附件节点缺少附件资源",
                "",
                node.GetPath().Value,
                "bind"));
        }

        foreach (var child in node.Children)
        {
            CollectScriptAttachmentsRecursive(child, attachments);
        }
    }

    private void CleanupPreview(bool reloadAuthoringScene)
    {
        var errors = new List<Exception>();
        var scriptRuntime = _previewScriptRuntime;
        try
        {
            scriptRuntime?.Dispose();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
        finally
        {
            if (scriptRuntime is not null)
            {
                _previewEngineContext?.RemoveService(scriptRuntime);
            }

            _previewScriptRuntime = null;
        }

        try
        {
            Tree.ReplaceChildren(new Node { Name = "PreviewCleanup" });
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            ReleasePreviewBootstrapLoadContext();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            if (_pluginManager.IsStarted)
            {
                _pluginManager.Stop();
            }
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
        finally
        {
            _previewContentService = null;
            _previewEngineContext = null;
            _isPreviewing = false;
        }

        if (reloadAuthoringScene)
        {
            try
            {
                Tree.LifecycleEnabled = false;
                LoadAuthoringScene(CurrentSceneId);
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        if (errors.Count > 0)
        {
            throw new AggregateException("项目预览清理不完整", errors);
        }
    }

    private static Camera3D? FindCamera(Node node)
    {
        if (node is Camera3D camera)
        {
            return camera;
        }

        foreach (var child in node.Children)
        {
            var found = FindCamera(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void LoadAuthoringScene(string? sceneId = null)
    {
        Tree.LifecycleEnabled = false;
        var id = string.IsNullOrWhiteSpace(sceneId) ? CurrentSceneId : sceneId;
        var authoredRoot = ProjectIO.LoadScene(Project, id, SceneRegistry);
        InjectServices(authoredRoot);
        Tree.ReplaceChildren(authoredRoot);
        CurrentSceneId = Project.SceneCatalog.GetById(id).Id;
        Tree.Paused = true;
        _isPreviewing = false;
    }

    /// <summary>按清单加载编辑器集成入口，只注册项目作者类型，不配置运行时服务。</summary>
    private void LoadEditorProjectTypes()
    {
        var reference = Project.Manifest.Editor;
        if (reference is null)
        {
            return;
        }

        var assemblyPath = ProjectIO.ResolveProjectPath(Project.RootDirectory, reference.Assembly);
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException(
                $"找不到项目 editor 程序集: {reference.Assembly}",
                assemblyPath);
        }

        try
        {
            var loadContext = new EditorProjectLoadContext(assemblyPath, Project.RootDirectory);
            _editorProjectLoadContext = loadContext;
            var assemblyBytes = ProjectIO.ReadProjectFileBytes(Project.RootDirectory, reference.Assembly);
            using var assemblyStream = new MemoryStream(assemblyBytes, writable: false);
            var assembly = loadContext.LoadFromStream(assemblyStream);
            var type = assembly.GetType(reference.Type, throwOnError: false, ignoreCase: false)
                ?? throw new InvalidOperationException($"项目 editor 类型不存在: {reference.Type} ({reference.Assembly})");
            if (!type.IsPublic || type.IsAbstract || type.IsInterface ||
                !typeof(IProjectRuntimeBootstrap).IsAssignableFrom(type))
            {
                throw new InvalidOperationException(
                    $"项目 editor 类型必须是公开具体的 {nameof(IProjectRuntimeBootstrap)}: {reference.Type}");
            }

            var constructor = type.GetConstructor(Type.EmptyTypes)
                ?? throw new InvalidOperationException($"项目 editor 类型必须提供公共无参构造: {reference.Type}");
            var bootstrap = (IProjectRuntimeBootstrap)(constructor.Invoke(null)
                ?? throw new InvalidOperationException($"创建项目 editor 类型返回空值: {reference.Type}"));
            bootstrap.RegisterProjectTypes(Project, SceneRegistry);
            if (bootstrap is IProjectEditorExtensionSource editorSource)
            {
                editorSource.RegisterProjectEditorExtensions(Project, ProjectEditorRegistry);
            }

            ProjectEditorRegistry.Commit();
        }
        catch
        {
            ReleaseEditorProjectLoadContext();
            throw;
        }
    }

    private void ConfigurePreview(Node previewRoot)
    {
        try
        {
            ConfigurePreviewCore(previewRoot);
        }
        catch
        {
            ReleasePreviewBootstrapLoadContext();
            throw;
        }
    }

    private void ConfigurePreviewCore(Node previewRoot)
    {
        var reference = Project.Manifest.Preview
            ?? throw new InvalidOperationException("项目没有声明 preview 入口");
        if (!ProjectIO.ProjectFileExists(Project.RootDirectory, reference.Assembly))
        {
            throw new FileNotFoundException(
                $"找不到项目预览程序集: {reference.Assembly}",
                ProjectIO.ResolveProjectPath(Project.RootDirectory, reference.Assembly));
        }

        Assembly assembly;
        try
        {
            var assemblyPath = ProjectIO.ResolveProjectPath(Project.RootDirectory, reference.Assembly);
            var assemblyBytes = ProjectIO.ReadProjectFileBytes(Project.RootDirectory, reference.Assembly);
            // bootstrap 与脚本一样使用可收集上下文；共享 Core 类型，停止预览后可卸载项目代码。
            var loadContext = new PreviewBootstrapLoadContext(assemblyPath, Project.RootDirectory);
            _previewBootstrapLoadContext = loadContext;
            _previewBootstrapLoadContextReference = new WeakReference(loadContext);
            using var assemblyStream = new MemoryStream(assemblyBytes, writable: false);
            assembly = loadContext.LoadFromStream(assemblyStream);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"加载项目预览程序集失败: {reference.Assembly}", exception);
        }

        var type = assembly.GetType(reference.Type, throwOnError: false, ignoreCase: false);
        if (type is null)
        {
            throw new InvalidOperationException($"项目预览类型不存在: {reference.Type} ({reference.Assembly})");
        }

        if (!type.IsPublic || type.IsAbstract || type.IsInterface ||
            !typeof(IProjectPreviewBootstrap).IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"项目预览类型必须是公开具体的 {nameof(IProjectPreviewBootstrap)}: {reference.Type}");
        }

        var constructor = type.GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException($"项目预览类型必须提供公共无参构造: {reference.Type}");
        IProjectPreviewBootstrap bootstrap;
        try
        {
            bootstrap = (IProjectPreviewBootstrap)(constructor.Invoke(null)
                ?? throw new InvalidOperationException($"项目预览类型实例化返回空值: {reference.Type}"));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"创建项目预览引导器失败: {reference.Type}", exception);
        }

        try
        {
            bootstrap.ConfigurePreview(new ProjectPreviewContext(Project, previewRoot));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"配置项目预览失败: {reference.Type}", exception);
        }
    }

    private void InjectServices(Node node)
    {
        if (node is IRenderingServerConsumer consumer)
        {
            consumer.Server = RenderingServer;
        }

        if (node is IJobSystemConsumer jobsConsumer)
        {
            jobsConsumer.Jobs = _jobs;
        }

        foreach (var child in node.Children)
        {
            InjectServices(child);
        }
    }

    private void ReleasePreviewBootstrapLoadContext()
    {
        var loadContext = _previewBootstrapLoadContext;
        _previewBootstrapLoadContext = null;
        loadContext?.Unload();
    }

    private void ReleaseEditorProjectLoadContext()
    {
        var loadContext = _editorProjectLoadContext;
        _editorProjectLoadContext = null;
        loadContext?.Unload();
    }

    private sealed class PreviewBootstrapLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _projectRoot;

        public PreviewBootstrapLoadContext(string assemblyPath, string projectRoot)
            : base($"CubitPreview:{Path.GetFileNameWithoutExtension(assemblyPath)}:{Guid.NewGuid():N}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(assemblyPath);
            _projectRoot = projectRoot;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is
                "Cubit.Core" or
                "Cubit.Physics" or
                "Cubit.Animation" or
                "Cubit.Audio" or
                "Cubit.Content" or
                "Cubit.Scripting" or
                "Cubit.Voxel")
            {
                return Assembly.Load(assemblyName);
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is null)
            {
                return null;
            }

            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(_projectRoot, fullPath);
            if (relative == "." || relative == ".." ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
            {
                throw new InvalidDataException("预览 bootstrap 依赖必须位于项目根目录内");
            }

            var dependencyBytes = ProjectIO.ReadProjectFileBytes(_projectRoot, relative);
            using var dependencyStream = new MemoryStream(dependencyBytes, writable: false);
            return LoadFromStream(dependencyStream);
        }
    }

    private sealed class EditorProjectLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _projectRoot;

        public EditorProjectLoadContext(string assemblyPath, string projectRoot)
            : base($"CubitEditorProject:{Path.GetFileNameWithoutExtension(assemblyPath)}:{Guid.NewGuid():N}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(assemblyPath);
            _projectRoot = projectRoot;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is
                "Cubit.Core" or
                "Cubit.Physics" or
                "Cubit.Animation" or
                "Cubit.Audio" or
                "Cubit.Content" or
                "Cubit.Scripting" or
                "Cubit.Voxel" or
                "Cubit.Mcp" or
                "Cubit.Mcp.Physics" or
                "Cubit.Mcp.Animation" or
                "Cubit.Mcp.Voxel" or
                "Cubit.Mcp.Audio" or
                "Cubit.Mcp.Content" or
                "Cubit.Mcp.Scripting" or
                "Cubit.Mcp.Ecs")
            {
                return Assembly.Load(assemblyName);
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is null)
            {
                return null;
            }

            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(_projectRoot, fullPath);
            if (relative == "." || relative == ".." ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
            {
                throw new InvalidDataException("项目 editor 依赖必须位于项目根目录内");
            }

            var dependencyBytes = ProjectIO.ReadProjectFileBytes(_projectRoot, relative);
            using var dependencyStream = new MemoryStream(dependencyBytes, writable: false);
            return LoadFromStream(dependencyStream);
        }
    }
}
