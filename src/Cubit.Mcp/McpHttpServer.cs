using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cubit.Mcp;

/// <summary>
/// MCP HTTP 传输：JSON-RPC over HTTP（POST /mcp，GET /health）。
/// 基于 TcpListener 的最小 HTTP 服务器：无第三方依赖、无 URL ACL 限制，可跨平台/移动端使用。
/// 核心复用 <see cref="McpServer.HandleRequest"/>。
/// </summary>
public sealed class McpHttpServer : IDisposable
{
    private readonly McpServer _server;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public McpHttpServer(IMcpEngineHost host, int port = 0)
    {
        _server = new McpServer(host, Stream.Null, Stream.Null);
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoop);
    }

    /// <summary>实际绑定端口（port=0 时由系统分配）。</summary>
    public int Port { get; }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleClient(client));
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                var header = new StringBuilder();
                var buffer = new byte[1];
                var headerDone = false;
                while (header.Length < 64 * 1024)
                {
                    var n = stream.Read(buffer, 0, 1);
                    if (n == 0)
                    {
                        return;
                    }

                    header.Append((char)buffer[0]);
                    if (header.Length >= 4 && header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        headerDone = true;
                        break;
                    }
                }

                if (!headerDone)
                {
                    return;
                }

                var head = header.ToString();
                var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0)
                {
                    return;
                }

                var requestLine = lines[0].Split(' ');
                if (requestLine.Length < 2)
                {
                    return;
                }

                var method = requestLine[0];
                var path = requestLine[1];
                var contentLength = 0;
                foreach (var line in lines.Skip(1))
                {
                    var idx = line.IndexOf(':');
                    if (idx > 0 && line[..idx].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = int.TryParse(line[(idx + 1)..].Trim(), out contentLength);
                    }
                }

                if (method == "OPTIONS")
                {
                    // CORS 预检：编辑器 Web 页面跨源调用
                    WriteResponse(stream, 204, "", "application/json");
                    return;
                }

                if (method == "GET" && path == "/health")
                {
                    WriteResponse(stream, 200, "ok", "text/plain");
                    return;
                }

                if (method == "POST" && path == "/mcp")
                {
                    var body = contentLength > 0 ? ReadExactly(stream, contentLength) : "";
                    string? response;
                    try
                    {
                        response = _server.HandleRequest(body);
                    }
                    catch (Exception ex)
                    {
                        response = "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32603,\"message\":\"" + ex.Message.Replace("\"", "'") + "\"}}";
                    }

                    if (response is null)
                    {
                        WriteResponse(stream, 204, "", "application/json");
                    }
                    else
                    {
                        WriteResponse(stream, 200, response, "application/json");
                    }

                    return;
                }

                WriteResponse(stream, 404, "not found", "text/plain");
            }
            catch
            {
                // 单连接错误不影响服务器
            }
        }
    }

    private static string ReadExactly(Stream stream, int count)
    {
        var bytes = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(bytes, read, count - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        return Encoding.UTF8.GetString(bytes, 0, read);
    }

    private static void WriteResponse(Stream stream, int status, string body, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var reason = status switch { 200 => "OK", 204 => "No Content", 404 => "Not Found", _ => "OK" };
        var head = $"HTTP/1.1 {status} {reason}\r\n" +
                   $"Access-Control-Allow-Origin: *\r\n" +
                   $"Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                   $"Access-Control-Allow-Headers: Content-Type\r\n" +
                   $"Content-Type: {contentType}\r\n" +
                   $"Content-Length: {bytes.Length}\r\n" +
                   "Connection: close\r\n\r\n";
        var headBytes = Encoding.ASCII.GetBytes(head);
        stream.Write(headBytes, 0, headBytes.Length);
        if (bytes.Length > 0)
        {
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // 忽略
        }

        _server.Dispose();
    }
}