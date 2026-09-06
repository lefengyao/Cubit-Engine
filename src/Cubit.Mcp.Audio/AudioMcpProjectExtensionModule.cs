using Cubit.Audio;
using Cubit.Mcp;

namespace Cubit.Mcp.Audio;

/// <summary>按项目清单装配 Audio MCP adapter，不创建音频服务或输出设备。</summary>
public sealed class AudioMcpProjectExtensionModule : IMcpProjectExtensionModule
{
    public string PluginId => "cubit.audio";

    public string ExpectedAssemblyName => "Cubit.Mcp.Audio";

    public bool AlwaysAvailable => false;

    public IMcpExtensionProvider Create(McpProjectExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new AudioMcpExtension(
            context.Tree,
            () => context.TryGetRuntimeService<AudioServer>(out var server) ? server : null);
    }
}
