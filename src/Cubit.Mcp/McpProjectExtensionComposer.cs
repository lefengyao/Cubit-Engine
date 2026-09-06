using System.Text.Json;
using Cubit.Core.Plugins;
using Cubit.Core.Project;
using Cubit.Core.Scene;

namespace Cubit.Mcp;

/// <summary>项目 MCP adapter 创建时可使用的通用运行上下文。</summary>
public sealed class McpProjectExtensionContext
{
    public required CubitProject Project { get; init; }

    public required SceneTree Tree { get; init; }

    public required EngineContext EngineContext { get; init; }

    public required Func<Type, object?> ResolveRuntimeService { get; init; }

    public bool TryGetRuntimeService<T>(out T? service) where T : class
    {
        var resolver = ResolveRuntimeService
            ?? throw new InvalidOperationException("MCP 项目扩展缺少运行服务解析器");
        service = resolver(typeof(T)) as T;
        return service is not null;
    }
}

/// <summary>由宿主显式链接的项目 MCP adapter 模块。</summary>
public interface IMcpProjectExtensionModule
{
    string PluginId { get; }

    string ExpectedAssemblyName { get; }

    bool AlwaysAvailable { get; }

    IMcpExtensionProvider Create(McpProjectExtensionContext context);
}

/// <summary>由项目 runtime bootstrap 显式提供的领域 MCP adapter 来源。</summary>
public interface IProjectMcpExtensionSource
{
    IEnumerable<IMcpProjectExtensionModule> CreateMcpExtensionModules();
}

/// <summary>一个项目声明与其 MCP adapter 的可用性诊断。</summary>
public sealed record McpProjectExtensionAdapterDiagnostic(string PluginId, string ExpectedAssemblyName);

/// <summary>项目 MCP adapter 的确定性组合结果。</summary>
public sealed class McpProjectExtensionComposition : IMcpExtensionProvider
{
    private const string StatusResourceUri = "cubit://mcp/extensions";
    private readonly IReadOnlyList<IMcpExtensionProvider> _providers;

    internal McpProjectExtensionComposition(
        IReadOnlyList<IMcpExtensionProvider> providers,
        IReadOnlyList<McpProjectExtensionAdapterDiagnostic> activeAdapters,
        IReadOnlyList<McpProjectExtensionAdapterDiagnostic> unavailableAdapters)
    {
        _providers = providers;
        ActiveAdapters = activeAdapters;
        UnavailableAdapters = unavailableAdapters;
    }

    public IReadOnlyList<McpProjectExtensionAdapterDiagnostic> ActiveAdapters { get; }

    public IReadOnlyList<McpProjectExtensionAdapterDiagnostic> UnavailableAdapters { get; }

    public IEnumerable<McpToolDescriptor> GetTools()
    {
        foreach (var provider in _providers)
        {
            foreach (var tool in provider.GetTools())
            {
                yield return tool;
            }
        }
    }

    public IEnumerable<McpResourceDescriptor> GetResources()
    {
        yield return new McpResourceDescriptor(
            StatusResourceUri,
            "项目 MCP adapter 状态",
            "application/json",
            DescribeAdapters);

        foreach (var provider in _providers)
        {
            foreach (var resource in provider.GetResources())
            {
                yield return resource;
            }
        }
    }

    public IEnumerable<McpPromptDescriptor> GetPrompts()
    {
        foreach (var provider in _providers)
        {
            foreach (var prompt in provider.GetPrompts())
            {
                yield return prompt;
            }
        }
    }

    private string DescribeAdapters() => JsonSerializer.Serialize(new
    {
        activeAdapters = ActiveAdapters.Select(adapter => new
        {
            pluginId = adapter.PluginId,
            expectedAssemblyName = adapter.ExpectedAssemblyName,
        }),
        unavailableAdapters = UnavailableAdapters.Select(adapter => new
        {
            pluginId = adapter.PluginId,
            expectedAssemblyName = adapter.ExpectedAssemblyName,
        }),
    });
}

/// <summary>根据项目清单选择宿主已链接的 MCP adapter，不发现或加载额外程序集。</summary>
public static class McpProjectExtensionComposer
{
    private const string CoreEcsPluginId = "cubit.core.ecs";

    public static McpProjectExtensionComposition Compose(
        McpProjectExtensionContext context,
        IEnumerable<IMcpProjectExtensionModule> modules)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(modules);

        var modulesByPluginId = IndexModules(modules);
        var providers = new List<IMcpExtensionProvider>();
        var activeAdapters = new List<McpProjectExtensionAdapterDiagnostic>();
        var unavailableAdapters = new List<McpProjectExtensionAdapterDiagnostic>();
        var selectedPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in context.Project.Manifest.Plugins)
        {
            ArgumentNullException.ThrowIfNull(plugin);
            var pluginId = NormalizeId(plugin.Id, "项目 MCP 插件 ID 不能为空");
            if (!selectedPluginIds.Add(pluginId))
            {
                throw new InvalidOperationException($"项目 MCP 插件重复声明: {pluginId}");
            }

            if (modulesByPluginId.TryGetValue(pluginId, out var module))
            {
                AddModule(module, pluginId, context, providers, activeAdapters);
            }
            else if (McpOfficialAdapterCatalog.TryGetExpectedAssemblyName(pluginId, out var expectedAssemblyName))
            {
                unavailableAdapters.Add(new McpProjectExtensionAdapterDiagnostic(
                    pluginId,
                    expectedAssemblyName));
            }
        }

        if (modulesByPluginId.TryGetValue(CoreEcsPluginId, out var coreEcsModule) &&
            coreEcsModule.AlwaysAvailable && selectedPluginIds.Add(CoreEcsPluginId))
        {
            AddModule(coreEcsModule, CoreEcsPluginId, context, providers, activeAdapters);
        }

        return new McpProjectExtensionComposition(
            Array.AsReadOnly(providers.ToArray()),
            Array.AsReadOnly(activeAdapters.ToArray()),
            Array.AsReadOnly(unavailableAdapters.ToArray()));
    }

    private static Dictionary<string, IMcpProjectExtensionModule> IndexModules(
        IEnumerable<IMcpProjectExtensionModule> modules)
    {
        var modulesByPluginId = new Dictionary<string, IMcpProjectExtensionModule>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            if (module is null)
            {
                throw new ArgumentException("MCP 项目 adapter 模块不能为 null", nameof(modules));
            }

            var pluginId = NormalizeId(
                module.PluginId,
                $"MCP 项目 adapter 模块 {module.GetType().FullName} 的 PluginId 不能为空");

            if (!modulesByPluginId.TryAdd(pluginId, module))
            {
                throw new ArgumentException(
                    $"MCP 项目 adapter 模块 PluginId 重复: {pluginId}",
                    nameof(modules));
            }
        }

        return modulesByPluginId;
    }

    private static void AddModule(
        IMcpProjectExtensionModule module,
        string pluginId,
        McpProjectExtensionContext context,
        ICollection<IMcpExtensionProvider> providers,
        ICollection<McpProjectExtensionAdapterDiagnostic> activeAdapters)
    {
        if (string.IsNullOrWhiteSpace(module.ExpectedAssemblyName))
        {
            throw new ArgumentException(
                $"MCP 项目 adapter 模块 {module.PluginId} 缺少 ExpectedAssemblyName",
                nameof(module));
        }

        var provider = module.Create(context)
            ?? throw new InvalidOperationException($"MCP 项目 adapter 模块 {module.PluginId} 返回了 null provider");
        providers.Add(provider);
        activeAdapters.Add(new McpProjectExtensionAdapterDiagnostic(pluginId, module.ExpectedAssemblyName));
    }

    private static string NormalizeId(string? id, string message) =>
        string.IsNullOrWhiteSpace(id) ? throw new ArgumentException(message, nameof(id)) : id.Trim();
}

/// <summary>官方 MCP adapter 的稳定清单；只保存 ID 与程序集名称，不引入领域程序集依赖。</summary>
internal static class McpOfficialAdapterCatalog
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedAssemblies =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cubit.voxel"] = "Cubit.Mcp.Voxel",
            ["cubit.physics"] = "Cubit.Mcp.Physics",
            ["cubit.animation"] = "Cubit.Mcp.Animation",
            ["cubit.audio"] = "Cubit.Mcp.Audio",
            ["cubit.content"] = "Cubit.Mcp.Content",
            ["cubit.scripting"] = "Cubit.Mcp.Scripting",
        };

    public static bool TryGetExpectedAssemblyName(string pluginId, out string expectedAssemblyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return ExpectedAssemblies.TryGetValue(pluginId, out expectedAssemblyName!);
    }
}
