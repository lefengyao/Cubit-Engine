using Cubit.Mcp;

namespace Cubit.Mcp.OfficialModules;

/// <summary>
/// 官方 MCP adapter 的聚合 facade。
/// 不包含领域工具实现，也不创建节点或服务；能力选择全部委托给项目清单 composer。
/// </summary>
public sealed class OfficialModulesMcpExtension : IMcpExtensionProvider
{
    private readonly McpProjectExtensionComposition _composition;

    public OfficialModulesMcpExtension(
        McpProjectExtensionContext context,
        IEnumerable<IMcpProjectExtensionModule> modules)
    {
        _composition = McpProjectExtensionComposer.Compose(
            context ?? throw new ArgumentNullException(nameof(context)),
            modules ?? throw new ArgumentNullException(nameof(modules)));
    }

    public IEnumerable<McpToolDescriptor> GetTools() => _composition.GetTools();

    public IEnumerable<McpResourceDescriptor> GetResources() => _composition.GetResources();

    public IEnumerable<McpPromptDescriptor> GetPrompts() => _composition.GetPrompts();
}
