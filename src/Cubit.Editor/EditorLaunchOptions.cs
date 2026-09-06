namespace Cubit.Editor;

public static class EditorWindowPolicy
{
    public static int InitialWidth => 1200;

    public static int InitialHeight => 720;
}

/// <summary>编辑器启动参数；无效项目参数降级为空项目而不是终止进程。</summary>
public sealed record EditorLaunchOptions(string? ProjectPath, bool McpStdio, int? McpHttpPort, string? Error)
{
    public static EditorLaunchOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? projectPath = null;
        var mcpStdio = false;
        int? mcpHttpPort = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--project", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    return new EditorLaunchOptions(null, false, null, "--project 缺少项目目录；编辑器将保持空项目");
                }

                projectPath = args[++i];
                continue;
            }

            if (args[i].Equals("--mcp", StringComparison.OrdinalIgnoreCase))
            {
                mcpStdio = true;
                continue;
            }

            if (args[i].Equals("--mcp-http", StringComparison.OrdinalIgnoreCase))
            {
                var port = 8765;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    if (!int.TryParse(args[++i], out port) || port is < 0 or > 65535)
                    {
                        return new EditorLaunchOptions(projectPath, false, null, "--mcp-http 端口必须在 0 到 65535 之间");
                    }
                }

                mcpHttpPort = port;
            }
        }

        if (mcpStdio && mcpHttpPort is not null)
        {
            return new EditorLaunchOptions(projectPath, false, null, "--mcp 与 --mcp-http 不能同时使用");
        }

        return new EditorLaunchOptions(projectPath, mcpStdio, mcpHttpPort, null);
    }
}
