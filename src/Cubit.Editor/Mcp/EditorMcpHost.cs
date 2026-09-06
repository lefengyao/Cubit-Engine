using System.Reflection;
using System.Text.Json;
using Cubit.Audio;
using Cubit.Content;
using Cubit.Core.ObjectSystem;
using Cubit.Core.Plugins;
using Cubit.Core.Rendering;
using Cubit.Core.Scene;
using Cubit.Editor.Scenes;
using Cubit.Mcp;
using Cubit.Mcp.Animation;
using Cubit.Mcp.Audio;
using Cubit.Mcp.Content;
using Cubit.Mcp.Ecs;
using Cubit.Mcp.Physics;
using Cubit.Mcp.Scripting;
using Cubit.Mcp.Voxel;
using Cubit.Scripting;

namespace Cubit.Editor.Mcp;

/// <summary>
/// Editor 的 MCP 宿主。打开项目时只操作作者场景；官方运行模块只在显式预览期间公开。
/// </summary>
public sealed class EditorMcpHost : IMcpEngineHost, IMcpExtensionProvider, IMcpRequestDispatcher, IDisposable
{
    private static readonly IReadOnlyList<McpContentImporterInfo> ContentImporters =
    [
        new McpContentImporterInfo("cubit.scene-json", "1.0.0", [".cscene"]),
        new McpContentImporterInfo("cubit.png-rgba8", "1.0.0", [".png"]),
    ];

    private readonly Func<IEditorSceneSession?> _sessionProvider;
    private readonly Action<string> _openProject;
    private readonly VulkanContext? _vulkan;
    private readonly MainThreadMcpDispatcher _dispatcher = new();

    public EditorMcpHost(
        Func<IEditorSceneSession?> sessionProvider,
        Action<string> openProject,
        VulkanContext? vulkan = null)
    {
        _sessionProvider = sessionProvider ?? throw new ArgumentNullException(nameof(sessionProvider));
        _openProject = openProject ?? throw new ArgumentNullException(nameof(openProject));
        _vulkan = vulkan;
    }

    /// <summary>由 Editor 帧循环在 UI/引擎主线程调用。</summary>
    public int PumpMcpRequests(int maximumRequests = 32) => _dispatcher.Pump(maximumRequests);

    public string? DispatchMcpRequest(Func<string?> request) => _dispatcher.DispatchMcpRequest(request);

    public string EngineInfo()
    {
        var session = CurrentSession;
        return JsonSerializer.Serialize(new
        {
            engine = "Cubit.Editor",
            projectOpen = session is not null,
            project = session?.DisplayName,
            previewing = session is IEditorPreviewSession preview && preview.IsPreviewing,
            services = session is null
                ? Array.Empty<object>()
                : new object[]
                {
                    new { name = "SceneTree", state = new { paused = session.Tree.Paused, ticksPerSecond = session.Tree.SimulationTicksPerSecond } },
                    new { name = "RenderingServer", state = new { meshCount = session.RenderingServer.MeshCount } },
                },
        });
    }

    public string DescribeScene()
    {
        var session = CurrentSession;
        return session is null
            ? JsonSerializer.Serialize(new { projectOpen = false, root = (object?)null })
            : JsonSerializer.Serialize(DescribeNode(session.Tree.Root));
    }

    public string GetSceneProperty(string nodePath, string property)
    {
        var node = RequireNode(nodePath);
        var info = FindProperty(node, property);
        return info is null
            ? throw new InvalidOperationException($"属性不存在: {property}")
            : JsonSerializer.Serialize(info.GetValue(node));
    }

    public string SetSceneProperty(string nodePath, string property, string jsonValue)
    {
        EnsureAuthoringState();
        var node = RequireNode(nodePath);
        var info = FindProperty(node, property);
        if (info is null || !info.CanWrite)
        {
            throw new InvalidOperationException($"属性不可写: {property}");
        }

        info.SetValue(node, JsonSerializer.Deserialize(jsonValue, info.PropertyType));
        return "ok";
    }

    public string ListSceneProperties(string nodePath)
    {
        var node = RequireNode(nodePath);
        var properties = node.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead &&
                (property.GetCustomAttribute<ExportAttribute>() is not null || IsEditableType(property.PropertyType)))
            .Select(property => new
            {
                name = property.Name,
                type = property.PropertyType.Name,
                exported = property.GetCustomAttribute<ExportAttribute>() is not null,
                value = SafeGet(property, node),
            });
        return JsonSerializer.Serialize(properties);
    }

    public string CallSceneMethod(string nodePath, string method, string? jsonArgs)
    {
        EnsureAuthoringState();
        var node = RequireNode(nodePath);
        var invocation = node.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
            ?? throw new InvalidOperationException($"无公开无参方法: {method}");
        var result = invocation.Invoke(node, null);
        return result is null ? "null" : JsonSerializer.Serialize(result);
    }

    public string ListGroups() => JsonSerializer.Serialize(RequireProjectSession().Tree.Groups);

    public string AddSceneNode(string parentPath, string type, string name)
    {
        var session = RequireProjectSession();
        EnsureAuthoringState(session);
        var parent = RequireNode(parentPath);
        var node = session.SceneRegistry.InstantiateNode(type)
            ?? throw new InvalidOperationException($"未知节点类型: {type}");
        node.Name = string.IsNullOrWhiteSpace(name) ? type : name;
        session.InjectEditorServices(node);
        parent.AddChild(node);
        return $"ok: {parent.GetPath().Value}/{node.Name}";
    }

    public string RemoveSceneNode(string nodePath)
    {
        var session = RequireProjectSession();
        EnsureAuthoringState(session);
        var node = RequireNode(nodePath);
        if (node == session.Tree.Root)
        {
            throw new InvalidOperationException("不能移除作者场景根节点");
        }

        node.Parent?.RemoveChild(node);
        return "ok";
    }

    public string SaveScene(string path)
    {
        var session = RequireProjectSession();
        EnsureAuthoringState(session);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("场景路径或 ID 不能为空", nameof(path));
        }

        var requestedId = ResolveSceneId(session, path);
        if (!string.Equals(requestedId, session.CurrentSceneId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Editor MCP 只能保存当前作者场景；请先调用 scene.load");
        }

        session.Save();
        return $"saved: {session.ScenePath}";
    }

    public string LoadScene(string path, bool apply)
    {
        var session = RequireProjectSession();
        EnsureAuthoringState(session);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("场景路径或 ID 不能为空", nameof(path));
        }

        var sceneId = ResolveSceneId(session, path);

        if (apply)
        {
            session.OpenScene(sceneId);
            return JsonSerializer.Serialize(new { applied = true, tree = DescribeNode(session.Tree.Root) });
        }

        var preview = Cubit.Core.Project.ProjectIO.LoadScene(session.Project, sceneId, session.SceneRegistry);
        return JsonSerializer.Serialize(new { applied = false, tree = DescribeNode(preview) });
    }

    private static string ResolveSceneId(ProjectSceneSession session, string idOrPath)
    {
        if (session.Project.SceneCatalog.TryGetById(idOrPath, out var byId) && byId is not null)
        {
            return byId.Id;
        }

        var entry = session.Project.SceneCatalog.GetByPath(idOrPath);
        return entry.Id;
    }

    public string ResetScene()
    {
        var session = RequireProjectSession();
        EnsureAuthoringState(session);
        session.ResetAuthoringScene();
        return "ok";
    }

    public string RunStatus()
    {
        var session = CurrentSession;
        return JsonSerializer.Serialize(new
        {
            projectOpen = session is not null,
            previewing = session is IEditorPreviewSession preview && preview.IsPreviewing,
            canStartPreview = session is IEditorPreviewSession available && available.CanStartPreview,
            paused = session?.Tree.Paused,
            ticksPerSecond = session?.Tree.SimulationTicksPerSecond,
        });
    }

    public string RunCommand(string command) => command.Equals("status", StringComparison.OrdinalIgnoreCase)
        ? RunStatus()
        : throw new InvalidOperationException($"未知 Editor 运行命令: {command}");

    public string StepTick()
    {
        var session = RequirePreviewSession();
        session.ProcessFrame(session.Tree.PhysicsDelta);
        return "ok";
    }

    public string SetPaused(bool paused)
    {
        var session = RequirePreviewSession();
        session.Tree.Paused = paused;
        return "ok";
    }

    public string SetSpeed(float rate)
    {
        var session = RequirePreviewSession();
        session.Tree.SimulationTicksPerSecond = Math.Clamp((int)rate, 1, 240);
        return "ok";
    }

    public string InputSetAction(string action, bool pressed) => throw new NotSupportedException("Editor MCP 不注入游戏输入；请在运行项目宿主中调用 input 工具");

    public string InputSetMoveAxis(float x, float y) => throw new NotSupportedException("Editor MCP 不注入游戏输入；请在运行项目宿主中调用 input 工具");

    public string InputAddLookDelta(float dx, float dy) => throw new NotSupportedException("Editor MCP 不注入游戏输入；请在运行项目宿主中调用 input 工具");

    public string InputSnapshot() => throw new NotSupportedException("Editor MCP 没有游戏输入服务");

    public string CaptureScreenshot(string path)
    {
        if (_vulkan is null)
        {
            throw new InvalidOperationException("当前 Editor MCP 宿主没有 Vulkan 截图服务");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(Path.GetTempPath(), $"cubit_editor_mcp_{DateTime.UtcNow:HHmmssfff}.png");
        }

        _vulkan.CaptureScreenshot(path);
        return $"requested: {path}";
    }

    public string CaptureStats()
    {
        var session = CurrentSession;
        return JsonSerializer.Serialize(new
        {
            projectOpen = session is not null,
            meshCount = session?.RenderingServer.MeshCount ?? 0,
            framebuffer = session?.RenderingServer.FramebufferSize,
        });
    }

    public IEnumerable<McpToolDescriptor> GetTools()
    {
        yield return new McpToolDescriptor(
            "editor.open_project",
            "在 Editor 中显式打开 Cubit 项目；不会自动启动项目预览",
            new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } },
            OpenProject);
        yield return new McpToolDescriptor(
            "editor.preview.start",
            "显式启动当前项目预览，并在成功后公开预览运行时模块工具",
            EmptySchema(),
            _ => StartPreview());
        yield return new McpToolDescriptor(
            "editor.preview.stop",
            "停止当前项目预览、释放运行服务并恢复作者场景",
            EmptySchema(),
            _ => StopPreview());
        yield return new McpToolDescriptor(
            "editor.preview.status",
            "读取当前项目预览状态",
            EmptySchema(),
            _ => DescribePreviewStatus());

        var extension = GetProjectExtensionComposition();
        if (extension is not null)
        {
            foreach (var tool in extension.GetTools())
            {
                yield return tool;
            }
        }
    }

    public IEnumerable<McpResourceDescriptor> GetResources()
    {
        yield return new McpResourceDescriptor(
            "cubit://editor/preview",
            "Editor 项目预览状态",
            "application/json",
            DescribePreviewStatus);

        var extension = GetProjectExtensionComposition();
        if (extension is not null)
        {
            foreach (var resource in extension.GetResources())
            {
                yield return resource;
            }
        }
    }

    public IEnumerable<McpPromptDescriptor> GetPrompts()
    {
        yield return new McpPromptDescriptor(
            "editor-preview",
            "检查或控制 Editor 项目预览",
            Array.Empty<object>(),
            _ => "请先调用 editor.preview.status。若项目尚未预览，使用 editor.preview.start；预览结束后用 editor.preview.stop 恢复作者场景。");

        var extension = GetProjectExtensionComposition();
        if (extension is not null)
        {
            foreach (var prompt in extension.GetPrompts())
            {
                yield return prompt;
            }
        }
    }

    public void Dispose() => _dispatcher.Dispose();

    private string OpenProject(JsonElement arguments)
    {
        var path = Argument(arguments, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("缺少项目目录", nameof(arguments));
        }

        _openProject(path);
        return DescribePreviewStatus();
    }

    private string StartPreview()
    {
        var session = RequireProjectSession();
        if (!session.CanStartPreview)
        {
            throw new InvalidOperationException("当前项目没有声明 preview 入口");
        }

        session.StartPreview();
        return DescribePreviewStatus();
    }

    private string StopPreview()
    {
        RequireProjectSession().StopPreview();
        return DescribePreviewStatus();
    }

    private string DescribePreviewStatus()
    {
        var session = CurrentSession;
        return JsonSerializer.Serialize(new
        {
            projectOpen = session is not null,
            project = session?.DisplayName,
            canStartPreview = session is IEditorPreviewSession available && available.CanStartPreview,
            previewing = session is IEditorPreviewSession preview && preview.IsPreviewing,
            projectAdapterToolsAvailable = GetProjectExtensionComposition() is not null,
        });
    }

    /// <summary>预览运行时按项目清单组合已显式链接的官方 MCP adapter。</summary>
    private McpProjectExtensionComposition? GetProjectExtensionComposition()
    {
        if (CurrentSession is not ProjectSceneSession session ||
            !session.IsPreviewing ||
            session.PreviewEngineContext is not EngineContext context)
        {
            return null;
        }

        return McpProjectExtensionComposer.Compose(
            new McpProjectExtensionContext
            {
                Project = session.Project,
                Tree = session.Tree,
                EngineContext = context,
                ResolveRuntimeService = type => type == typeof(AudioServer)
                    ? context.TryGetService<AudioServer>(out var audioServer) ? audioServer : null
                    : type == typeof(ContentService)
                        ? context.TryGetService<ContentService>(out var contentService) ? contentService : null
                        : type == typeof(ScriptRuntime)
                            ? context.TryGetService<ScriptRuntime>(out var scriptRuntime) ? scriptRuntime : null
                            : null,
            },
            [
                new VoxelMcpProjectExtensionModule(),
                new PhysicsMcpProjectExtensionModule(),
                new AnimationMcpProjectExtensionModule(),
                new AudioMcpProjectExtensionModule(),
                new ContentMcpProjectExtensionModule(ContentImporters),
                new ScriptingMcpProjectExtensionModule(),
                new EcsMcpProjectExtensionModule(),
            ]);
    }

    private IEditorSceneSession? CurrentSession => _sessionProvider();

    private ProjectSceneSession RequireProjectSession() => CurrentSession as ProjectSceneSession
        ?? throw new InvalidOperationException("Editor 尚未打开 Cubit 项目");

    private ProjectSceneSession RequirePreviewSession()
    {
        var session = RequireProjectSession();
        if (!session.IsPreviewing)
        {
            throw new InvalidOperationException("当前项目未运行预览");
        }

        return session;
    }

    private void EnsureAuthoringState() => EnsureAuthoringState(RequireProjectSession());

    private static void EnsureAuthoringState(ProjectSceneSession session)
    {
        if (session.IsPreviewing)
        {
            throw new InvalidOperationException("运行项目预览时不能修改作者场景；请先调用 editor.preview.stop");
        }
    }

    private Node RequireNode(string nodePath)
    {
        if (string.IsNullOrWhiteSpace(nodePath))
        {
            throw new ArgumentException("节点路径不能为空", nameof(nodePath));
        }

        return RequireProjectSession().Tree.GetNode(new NodePath(nodePath))
            ?? throw new InvalidOperationException($"节点不存在: {nodePath}");
    }

    private static object DescribeNode(Node node) => new
    {
        name = node.Name,
        type = node.GetType().Name,
        path = node.GetPath().Value,
        children = node.Children.Select(DescribeNode).ToArray(),
    };

    private static PropertyInfo? FindProperty(Node node, string property) =>
        node.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);

    private static object? SafeGet(PropertyInfo property, object target)
    {
        try
        {
            return property.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsEditableType(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
        type == typeof(System.Numerics.Vector3) || type == typeof(Guid) || type == typeof(DateTime);

    private static bool PathsEqual(string first, string second) => string.Equals(
        Path.GetFullPath(first),
        Path.GetFullPath(second),
        StringComparison.OrdinalIgnoreCase);

    private static string Argument(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value)
            ? value.GetString() ?? ""
            : "";

    private static object EmptySchema() => new { type = "object", properties = new { } };
}
