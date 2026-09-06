using System.Text.Json;
using Cubit.Core.Scene;
using Cubit.Mcp;

namespace Cubit.Mcp.Ecs;

/// <summary>Core ECS 的 MCP adapter；不依赖项目插件清单。</summary>
public sealed class EcsMcpProjectExtensionModule : IMcpProjectExtensionModule
{
    public string PluginId => "cubit.core.ecs";

    public string ExpectedAssemblyName => "Cubit.Mcp.Ecs";

    public bool AlwaysAvailable => true;

    public IMcpExtensionProvider Create(McpProjectExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new EcsMcpExtension(context.Tree);
    }
}

/// <summary>只读取作者场景已有 EcsNode 的运行状态。</summary>
public sealed class EcsMcpExtension : IMcpExtensionProvider
{
    private readonly SceneTree _tree;

    public EcsMcpExtension(SceneTree tree)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
    }

    public IEnumerable<McpToolDescriptor> GetTools()
    {
        if (FindNodes<EcsNode>().Any())
        {
            yield return new McpToolDescriptor(
                "ecs.list_nodes",
                "列出场景中已存在 EcsNode 的实体与调度状态",
                new { type = "object", properties = new { } },
                _ => ListEcsNodes());
        }
    }

    public IEnumerable<McpResourceDescriptor> GetResources() => [];

    public IEnumerable<McpPromptDescriptor> GetPrompts() => [];

    private string ListEcsNodes() => JsonSerializer.Serialize(FindNodes<EcsNode>()
        .Select(node => new
        {
            nodePath = node.GetPath().Value,
            workerCount = node.Jobs?.WorkerCount ?? 0,
            configuredWorkerCount = node.WorkerCount,
            entityCount = node.World.EntityCount,
            isInitialized = node.IsInitialized,
            schedulerPresent = node.Scheduler is not null,
        }));

    private IEnumerable<T> FindNodes<T>() where T : Node => FindNodes<T>(_tree.Root);

    private static IEnumerable<T> FindNodes<T>(Node node) where T : Node
    {
        if (node is T match)
        {
            yield return match;
        }

        foreach (var child in node.Children)
        {
            foreach (var childMatch in FindNodes<T>(child))
            {
                yield return childMatch;
            }
        }
    }
}
