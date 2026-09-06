using System.Reflection;
using System.Text.Json;
using Cubit.Core.Input;
using Cubit.Core.Project;
using Cubit.Core.Rendering;
using Cubit.Core.Scene;
using Cubit.Mcp;

namespace Cubit.Sample;

/// <summary>通用项目运行会话到 MCP 基础能力的适配；领域工具由项目提供的 extension 输出。</summary>
public sealed class GameSceneMcpHost : IMcpEngineHost, IMcpRequestDispatcher, IDisposable
{
    private readonly GameScene _scene;
    private readonly VulkanContext? _vulkan;
    private readonly InputServer _input;
    private readonly MainThreadMcpDispatcher _dispatcher;

    public GameSceneMcpHost(
        GameScene scene,
        VulkanContext? vulkan = null,
        InputServer? input = null)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _vulkan = vulkan;
        _input = input ?? new InputServer();
        _dispatcher = new MainThreadMcpDispatcher();
    }

    public InputServer Input => _input;

    public int PumpMcpRequests(int maximumRequests = 32) => _dispatcher.Pump(maximumRequests);

    public string? DispatchMcpRequest(Func<string?> request) => _dispatcher.DispatchMcpRequest(request);

    public void Dispose() => _dispatcher.Dispose();

    public string EngineInfo() => JsonSerializer.Serialize(new
    {
        engine = "Cubit.Core",
        framework = "net9.0",
        services = new object[]
        {
            new
            {
                name = "SceneTree",
                type = "Cubit.Core.Scene.SceneTree",
                state = new
                {
                    simulationTicksPerSecond = _scene.Tree.SimulationTicksPerSecond,
                    paused = _scene.Tree.Paused,
                    rootChildren = _scene.Tree.Root.Children.Count,
                },
            },
            new
            {
                name = "RenderingServer",
                type = "Cubit.Core.Rendering.RenderingServer",
                state = new
                {
                    meshCount = _scene.RenderingServer.MeshCount,
                    framebufferWidth = _scene.RenderingServer.FramebufferSize.Width,
                    framebufferHeight = _scene.RenderingServer.FramebufferSize.Height,
                },
            },
            new { name = "InputServer", type = "Cubit.Core.Input.InputServer", state = new { snapshot = _input.BuildSnapshot() } },
        },
    });

    public string DescribeScene()
    {
        var root = new
        {
            name = _scene.Tree.Root.Name,
            type = _scene.Tree.Root.GetType().Name,
            path = "/",
            children = DescribeChildren(_scene.Tree.Root),
        };
        return JsonSerializer.Serialize(root);
    }

    public string GetSceneProperty(string nodePath, string property)
    {
        var node = Resolve(nodePath);
        var prop = FindProperty(node, property);
        return prop is null ? $"属性不存在: {property}" : JsonSerializer.Serialize(prop.GetValue(node));
    }

    public string SetSceneProperty(string nodePath, string property, string jsonValue)
    {
        var node = Resolve(nodePath);
        var prop = FindProperty(node, property);
        if (prop is null)
        {
            return $"属性不存在: {property}";
        }

        var value = JsonSerializer.Deserialize(jsonValue, prop.PropertyType);
        prop.SetValue(node, value);
        return "ok";
    }

    public string ListSceneProperties(string nodePath)
    {
        var node = Resolve(nodePath);
        var props = node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead &&
                (property.GetCustomAttribute<ExportAttribute>() is not null || IsEditableType(property.PropertyType)))
            .Select(property => new
            {
                name = property.Name,
                type = property.PropertyType.Name,
                exported = property.GetCustomAttribute<ExportAttribute>() is not null,
                value = SafeGet(property, node),
            })
            .ToArray();
        return JsonSerializer.Serialize(props);
    }

    public string CallSceneMethod(string nodePath, string method, string? jsonArgs)
    {
        var node = Resolve(nodePath);
        var methodInfo = node.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (methodInfo is null)
        {
            return $"方法不存在: {method}";
        }

        var result = methodInfo.Invoke(node, null);
        return result is null ? "null" : JsonSerializer.Serialize(result);
    }

    public string ListGroups() => JsonSerializer.Serialize(_scene.Tree.Groups);

    public string RunStatus() => JsonSerializer.Serialize(new
    {
        tickRate = _scene.Tree.SimulationTicksPerSecond,
        paused = _scene.Tree.Paused,
        nodes = CountNodes(_scene.Tree.Root),
        meshes = _scene.RenderingServer.MeshCount,
    });

    public string RunCommand(string command) => command switch
    {
        "status" => RunStatus(),
        _ => $"未知命令: {command}",
    };

    public string StepTick()
    {
        _scene.Tree.ProcessFrame(1d / _scene.Tree.SimulationTicksPerSecond);
        return "ok";
    }

    public string SetPaused(bool paused)
    {
        _scene.Tree.Paused = paused;
        return "ok";
    }

    public string SetSpeed(float rate)
    {
        _scene.Tree.SimulationTicksPerSecond = Math.Clamp((int)rate, 1, 240);
        return "ok";
    }

    public string CaptureScreenshot(string path)
    {
        if (_vulkan is null)
        {
            return "无 GPU 渲染器（无头宿主不支持截图）";
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(Path.GetTempPath(), $"cubit_mcp_{DateTime.Now:HHmmssfff}.png");
        }

        _vulkan.CaptureScreenshot(path);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return $"saved: {path}";
            }

            Thread.Sleep(50);
        }

        return $"等待超时: {path}";
    }

    public string CaptureStats() => JsonSerializer.Serialize(new
    {
        meshes = _scene.RenderingServer.MeshCount,
        framebuffer = _scene.RenderingServer.FramebufferSize,
    });

    public string AddSceneNode(string parentPath, string type, string name)
    {
        var parent = Resolve(parentPath);
        var node = _scene.Runtime.SceneRegistry.InstantiateNode(type);
        if (node is null)
        {
            return $"未知节点类型: {type}（已知: {string.Join(", ", _scene.Runtime.SceneRegistry.KnownNodeTypes)}）";
        }

        node.Name = string.IsNullOrWhiteSpace(name) ? type : name;
        parent.AddChild(node);
        return $"ok: {parentPath}/{node.Name}";
    }

    public string RemoveSceneNode(string nodePath)
    {
        var node = Resolve(nodePath);
        if (node == _scene.Tree.Root)
        {
            return "不能移除根节点";
        }

        node.Parent?.RemoveChild(node);
        return "ok";
    }

    public string SaveScene(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "缺少路径";
        }

        if (string.Equals(Path.GetFullPath(path), _scene.Runtime.Project.MainScenePath, StringComparison.OrdinalIgnoreCase))
        {
            ProjectIO.SaveMainScene(_scene.Runtime.Project, _scene.Tree.Root);
            return $"saved: {path}";
        }

        SceneIO.SaveToJson(_scene.Tree.Root, path);
        return $"saved: {path}";
    }

    public string LoadScene(string path, bool apply)
    {
        if (!File.Exists(path))
        {
            return $"文件不存在: {path}";
        }

        var root = SceneIO.LoadFromJson(path, _scene.Runtime.SceneRegistry);
        if (!apply)
        {
            return JsonSerializer.Serialize(new { name = root.Name, type = root.GetType().Name, children = root.Children.Count });
        }

        _scene.LoadSceneFile(path);
        return JsonSerializer.Serialize(new { applied = true, name = root.Name, children = root.Children.Count, tree = DescribeScene() });
    }

    public string ResetScene()
    {
        _scene.ReloadMainScene();
        return "ok";
    }

    public string InputSetAction(string action, bool pressed)
    {
        if (!Enum.TryParse<InputAction>(action, true, out var parsed))
        {
            return $"未知输入动作: {action}";
        }

        _input.SetAction(parsed, pressed);
        return "ok";
    }

    public string InputSetMoveAxis(float x, float y)
    {
        _input.SetMoveAxis(x, y);
        return "ok";
    }

    public string InputAddLookDelta(float dx, float dy)
    {
        _input.AddLookDelta(dx, dy);
        return "ok";
    }

    public string InputSnapshot()
    {
        var snapshot = _input.BuildSnapshot();
        return JsonSerializer.Serialize(new
        {
            forward = snapshot.Forward,
            back = snapshot.Back,
            left = snapshot.Left,
            right = snapshot.Right,
            up = snapshot.Up,
            down = snapshot.Down,
            cancel = snapshot.Cancel,
            mouseDeltaX = snapshot.MouseDeltaX,
            mouseDeltaY = snapshot.MouseDeltaY,
        });
    }

    private Node Resolve(string nodePath) => _scene.Tree.GetNode(new NodePath(nodePath))
        ?? throw new InvalidOperationException($"节点不存在: {nodePath}");

    private static object[] DescribeChildren(Node node) => node.Children.Select(child => new
    {
        name = child.Name,
        type = child.GetType().Name,
        path = child.GetPath().Value,
        children = DescribeChildren(child),
    }).Cast<object>().ToArray();

    private static PropertyInfo? FindProperty(Node node, string name) =>
        node.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

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

    private static int CountNodes(Node node) => 1 + node.Children.Sum(CountNodes);
}
