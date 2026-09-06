using System.Text.Json;

namespace Cubit.Mcp;

/// <summary>MCP 工具扩展描述；领域插件通过它追加自己的工具。</summary>
public sealed record McpToolDescriptor(
    string Name,
    string Description,
    object? InputSchema,
    Func<JsonElement, string> Handler);

/// <summary>MCP 资源扩展描述。</summary>
public sealed record McpResourceDescriptor(
    string Uri,
    string Name,
    string MimeType,
    Func<string> Read);

/// <summary>MCP 提示词扩展描述。</summary>
public sealed record McpPromptDescriptor(
    string Name,
    string Description,
    object Arguments,
    Func<JsonElement, string> Handler);

/// <summary>项目或官方插件向 MCP 核心追加领域工具、资源和提示词。</summary>
public interface IMcpExtensionProvider
{
    IEnumerable<McpToolDescriptor> GetTools();

    IEnumerable<McpResourceDescriptor> GetResources();

    IEnumerable<McpPromptDescriptor> GetPrompts();
}
