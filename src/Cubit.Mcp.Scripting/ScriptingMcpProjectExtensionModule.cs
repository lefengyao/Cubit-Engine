using System.Text.Json;
using Cubit.Mcp;
using Cubit.Scripting;

namespace Cubit.Mcp.Scripting;

/// <summary>按项目清单装配 Scripting MCP adapter。</summary>
public sealed class ScriptingMcpProjectExtensionModule : IMcpProjectExtensionModule
{
    public string PluginId => "cubit.scripting";

    public string ExpectedAssemblyName => "Cubit.Mcp.Scripting";

    public bool AlwaysAvailable => false;

    public IMcpExtensionProvider Create(McpProjectExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ScriptingMcpExtension(
            () => context.TryGetRuntimeService<ScriptRuntime>(out var runtime) ? runtime : null);
    }
}

/// <summary>只读取或停止宿主已启动的脚本运行时，不加载程序集或创建脚本。</summary>
public sealed class ScriptingMcpExtension : IMcpExtensionProvider
{
    private readonly Func<ScriptRuntime?> _runtimeResolver;

    public ScriptingMcpExtension(Func<ScriptRuntime?> runtimeResolver)
    {
        _runtimeResolver = runtimeResolver ?? throw new ArgumentNullException(nameof(runtimeResolver));
    }

    public IEnumerable<McpToolDescriptor> GetTools()
    {
        if (CurrentRuntime is null)
        {
            yield break;
        }

        yield return new McpToolDescriptor(
            "scripting.status",
            "读取已启动 ScriptRuntime 的状态，不加载或执行项目代码",
            new { type = "object", properties = new { } },
            _ => Status());
        yield return new McpToolDescriptor(
            "scripting.stop",
            "显式停止已运行的 ScriptRuntime，不加载或启动脚本",
            new { type = "object", properties = new { } },
            _ => Stop());
    }

    public IEnumerable<McpResourceDescriptor> GetResources() => [];

    public IEnumerable<McpPromptDescriptor> GetPrompts() => [];

    private ScriptRuntime? CurrentRuntime => _runtimeResolver();

    private string Status()
    {
        var runtime = CurrentRuntime ?? throw new InvalidOperationException("宿主未注入 ScriptRuntime");
        return JsonSerializer.Serialize(new
        {
            isRunning = runtime.IsRunning,
            loadedAssembly = runtime.LoadedAssembly?.GetName().Name,
            activeScripts = runtime.ActiveScripts.Select(script => new
            {
                type = script.GetType().FullName,
                nodePath = script.TargetNode?.GetPath().Value,
            }),
            diagnostics = runtime.Diagnostics.Snapshot().Select(diagnostic => new
            {
                code = diagnostic.Code,
                severity = diagnostic.Severity.ToString(),
                message = diagnostic.Message,
                sourcePath = diagnostic.SourcePath,
                nodePath = diagnostic.NodePath,
            }),
        });
    }

    private string Stop()
    {
        var runtime = CurrentRuntime ?? throw new InvalidOperationException("宿主未注入 ScriptRuntime");
        if (runtime.IsRunning)
        {
            runtime.Stop();
        }

        return "ok";
    }
}
