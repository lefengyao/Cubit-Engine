namespace Cubit.Mcp;

/// <summary>
/// 将 MCP 请求调度到宿主所有者线程的契约。
/// HTTP/stdio 传输线程不得直接访问 SceneTree、GPU 或平台服务。
/// </summary>
public interface IMcpRequestDispatcher
{
    /// <summary>在宿主允许的线程执行完整 MCP 请求并返回 JSON-RPC 响应。</summary>
    string? DispatchMcpRequest(Func<string?> request);
}
