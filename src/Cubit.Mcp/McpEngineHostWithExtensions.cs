namespace Cubit.Mcp;

/// <summary>
/// 为既有 MCP 宿主叠加任意数量的领域扩展。
/// MCP 核心只认识扩展契约，不引用任何官方模块或项目程序集。
/// </summary>
public sealed class McpEngineHostWithExtensions : IMcpEngineHost, IMcpExtensionProvider, IMcpRequestDispatcher
{
    private readonly IMcpEngineHost _inner;
    private readonly IReadOnlyList<IMcpExtensionProvider> _extensions;

    public McpEngineHostWithExtensions(
        IMcpEngineHost inner,
        IEnumerable<IMcpExtensionProvider> extensions)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(extensions);

        var providers = new List<IMcpExtensionProvider>();
        if (inner is IMcpExtensionProvider innerProvider)
        {
            providers.Add(innerProvider);
        }

        foreach (var extension in extensions)
        {
            providers.Add(extension ?? throw new ArgumentException("MCP 扩展不能为 null", nameof(extensions)));
        }

        _extensions = providers;
    }

    public string EngineInfo() => _inner.EngineInfo();

    public string DescribeScene() => _inner.DescribeScene();

    public string GetSceneProperty(string nodePath, string property) => _inner.GetSceneProperty(nodePath, property);

    public string SetSceneProperty(string nodePath, string property, string jsonValue) => _inner.SetSceneProperty(nodePath, property, jsonValue);

    public string ListSceneProperties(string nodePath) => _inner.ListSceneProperties(nodePath);

    public string CallSceneMethod(string nodePath, string method, string? jsonArgs) => _inner.CallSceneMethod(nodePath, method, jsonArgs);

    public string ListGroups() => _inner.ListGroups();

    public string AddSceneNode(string parentPath, string type, string name) => _inner.AddSceneNode(parentPath, type, name);

    public string RemoveSceneNode(string nodePath) => _inner.RemoveSceneNode(nodePath);

    public string SaveScene(string path) => _inner.SaveScene(path);

    public string LoadScene(string path, bool apply) => _inner.LoadScene(path, apply);

    public string ResetScene() => _inner.ResetScene();

    public string RunStatus() => _inner.RunStatus();

    public string RunCommand(string command) => _inner.RunCommand(command);

    public string StepTick() => _inner.StepTick();

    public string SetPaused(bool paused) => _inner.SetPaused(paused);

    public string SetSpeed(float rate) => _inner.SetSpeed(rate);

    public string InputSetAction(string action, bool pressed) => _inner.InputSetAction(action, pressed);

    public string InputSetMoveAxis(float x, float y) => _inner.InputSetMoveAxis(x, y);

    public string InputAddLookDelta(float dx, float dy) => _inner.InputAddLookDelta(dx, dy);

    public string InputSnapshot() => _inner.InputSnapshot();

    public string CaptureScreenshot(string path) => _inner.CaptureScreenshot(path);

    public string CaptureStats() => _inner.CaptureStats();

    public string? DispatchMcpRequest(Func<string?> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _inner is IMcpRequestDispatcher dispatcher
            ? dispatcher.DispatchMcpRequest(request)
            : request();
    }

    public IEnumerable<McpToolDescriptor> GetTools() => _extensions.SelectMany(provider => provider.GetTools());

    public IEnumerable<McpResourceDescriptor> GetResources() => _extensions.SelectMany(provider => provider.GetResources());

    public IEnumerable<McpPromptDescriptor> GetPrompts() => _extensions.SelectMany(provider => provider.GetPrompts());
}
