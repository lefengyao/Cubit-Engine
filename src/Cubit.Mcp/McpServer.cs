using System.Text;
using System.Text.Json;
using Cubit.Core.Project;

namespace Cubit.Mcp;

/// <summary>
/// 引擎级 MCP JSON-RPC 服务。核心只提供项目、场景、运行、通用输入和截图能力；
/// 领域工具由宿主通过 IMcpExtensionProvider 注入。
/// </summary>
public sealed class McpServer : IDisposable
{
    public const string ProtocolVersion = "2025-11-25";

    private readonly IMcpEngineHost _host;
    private readonly StreamReader _input;
    private readonly StreamWriter _output;
    private volatile bool _running = true;

    public McpServer(IMcpEngineHost host, Stream input, Stream output)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _input = new StreamReader(input ?? throw new ArgumentNullException(nameof(input)), new UTF8Encoding(false), false, 4096, leaveOpen: true);
        _output = new StreamWriter(output ?? throw new ArgumentNullException(nameof(output)), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

    }

    public void Run()
    {
        while (_running)
        {
            var line = _input.ReadLine();
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var response = HandleRequest(line);
                if (response is not null)
                {
                    _output.WriteLine(response);
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine(FormatError(null, -32603, ex.Message));
            }
        }
    }

    /// <summary>
    /// 处理一条 JSON-RPC 请求。若宿主声明线程调度器，整条请求（含扩展工具、资源与提示词）
    /// 必须在宿主所有者线程执行，避免传输线程直接访问场景或平台服务。
    /// </summary>
    public string? HandleRequest(string requestJson)
    {
        if (_host is IMcpRequestDispatcher dispatcher)
        {
            return dispatcher.DispatchMcpRequest(() => HandleRequestCore(requestJson));
        }

        return HandleRequestCore(requestJson);
    }

    private string? HandleRequestCore(string requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(requestJson);
        }
        catch (JsonException)
        {
            return FormatError(null, -32700, "Parse error");
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var methodElement) ||
                !root.TryGetProperty("id", out var idElement))
            {
                return null;
            }

            var method = methodElement.GetString();
            var parameters = root.TryGetProperty("params", out var parameterElement) ? parameterElement : default;
            return method switch
            {
                "initialize" => FormatResult(idElement, new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { tools = new { }, resources = new { }, prompts = new { } },
                    serverInfo = new { name = "Cubit", version = "1.0.0" },
                }),
                "ping" => FormatResult(idElement, new { }),
                "tools/list" => ListTools(idElement),
                "tools/call" => HandleToolCall(idElement, parameters),
                "resources/list" => ListResources(idElement),
                "resources/read" => HandleResourceRead(idElement, parameters),
                "prompts/list" => ListPrompts(idElement),
                "prompts/get" => HandlePromptGet(idElement, parameters),
                _ => FormatError(idElement, -32601, $"未知方法: {method}"),
            };
        }
    }

    public void Dispose()
    {
        _running = false;
        _input.Dispose();
        _output.Dispose();
    }

    private string HandleToolCall(JsonElement id, JsonElement parameters)
    {
        if (!parameters.TryGetProperty("name", out var nameElement))
        {
            return FormatError(id, -32602, "缺少参数 name");
        }

        var name = nameElement.GetString() ?? "";
        var arguments = parameters.TryGetProperty("arguments", out var argumentElement) && argumentElement.ValueKind == JsonValueKind.Object
            ? argumentElement
            : default;
        var tool = BuildCapabilities().Tools.FirstOrDefault(item => item.Name == name);
        if (tool is null)
        {
            return FormatError(id, -32601, $"未知工具: {name}");
        }

        try
        {
            return FormatResult(id, new
            {
                content = new[] { new { type = "text", text = tool.Handler(arguments) } },
                isError = false,
            });
        }
        catch (Exception ex)
        {
            return FormatResult(id, new
            {
                content = new[] { new { type = "text", text = ex.Message } },
                isError = true,
            });
        }
    }

    private string HandleResourceRead(JsonElement id, JsonElement parameters)
    {
        if (!parameters.TryGetProperty("uri", out var uriElement))
        {
            return FormatError(id, -32602, "缺少参数 uri");
        }

        var uri = uriElement.GetString() ?? "";
        var resource = BuildCapabilities().Resources.FirstOrDefault(item => item.Uri == uri);
        if (resource is null)
        {
            return FormatError(id, -32602, $"未知资源: {uri}");
        }

        return FormatResult(id, new
        {
            contents = new[] { new { uri, mimeType = resource.MimeType, text = resource.Read() } },
        });
    }

    private string HandlePromptGet(JsonElement id, JsonElement parameters)
    {
        if (!parameters.TryGetProperty("name", out var nameElement))
        {
            return FormatError(id, -32602, "缺少参数 name");
        }

        var name = nameElement.GetString() ?? "";
        var prompt = BuildCapabilities().Prompts.FirstOrDefault(item => item.Name == name);
        if (prompt is null)
        {
            return FormatError(id, -32602, $"未知提示词: {name}");
        }

        var arguments = parameters.TryGetProperty("arguments", out var argumentElement) && argumentElement.ValueKind == JsonValueKind.Object
            ? argumentElement
            : default;
        return FormatResult(id, new
        {
            description = prompt.Description,
            messages = new[]
            {
                new { role = "user", content = new { type = "text", text = prompt.Handler(arguments) } },
            },
        });
    }

    private List<McpToolDescriptor> BuildTools() =>
    [
        new("project.create", "创建正式的空 Cubit 项目（仅 Root，不覆盖现有内容）",
            new { type = "object", properties = new { path = P("string"), name = P("string") }, required = new[] { "path" } },
            CreateProject),
        new("scene.get_tree", "返回场景树 JSON（节点名/类型/路径/子节点）", EmptySchema(), _ => _host.DescribeScene()),
        new("engine.info", "引擎结构信息与运行状态 JSON", EmptySchema(), _ => _host.EngineInfo()),
        new("scene.get_property", "读取节点属性", Schema("nodePath", "property"), args => _host.GetSceneProperty(Arg(args, "nodePath"), Arg(args, "property"))),
        new("scene.list_properties", "列出节点可编辑属性", Schema("nodePath"), args => _host.ListSceneProperties(Arg(args, "nodePath"))),
        new("scene.set_property", "设置节点属性", Schema("nodePath", "property", "value"), args => _host.SetSceneProperty(Arg(args, "nodePath"), Arg(args, "property"), Arg(args, "value"))),
        new("scene.call_method", "调用节点无参方法", Schema("nodePath", "method"), args => _host.CallSceneMethod(Arg(args, "nodePath"), Arg(args, "method"), null)),
        new("scene.list_groups", "列出场景分组", EmptySchema(), _ => _host.ListGroups()),
        new("scene.add_node", "向父节点添加 ClassDB 已注册的节点类型", new { type = "object", properties = new { parentPath = P("string"), type = P("string"), name = P("string") } }, args => _host.AddSceneNode(Arg(args, "parentPath"), Arg(args, "type"), Arg(args, "name"))),
        new("scene.remove_node", "移除节点（根节点除外）", new { type = "object", properties = new { nodePath = P("string") } }, args => _host.RemoveSceneNode(Arg(args, "nodePath"))),
        new("scene.save", "保存场景树到 .cscene JSON", new { type = "object", properties = new { path = P("string") } }, args => _host.SaveScene(Arg(args, "path"))),
        new("scene.load", "加载 .cscene 场景文件", new { type = "object", properties = new { path = P("string"), apply = P("boolean") } }, args => _host.LoadScene(Arg(args, "path"), ArgBool(args, "apply", true))),
        new("scene.reset", "恢复宿主默认场景", EmptySchema(), _ => _host.ResetScene()),
        new("run.status", "引擎运行状态", EmptySchema(), _ => _host.RunStatus()),
        new("run.command", "执行引擎命令", Schema("command"), args => _host.RunCommand(Arg(args, "command"))),
        new("run.pause", "暂停/恢复模拟", new { type = "object", properties = new { paused = P("boolean") } }, args => _host.SetPaused(ArgBool(args, "paused", true))),
        new("run.set_speed", "设置模拟 tick 频率", new { type = "object", properties = new { rate = P("number") } }, args => _host.SetSpeed(ArgFloat(args, "rate", 20))),
        new("run.tick_step", "手动推进一个模拟 tick", EmptySchema(), _ => _host.StepTick()),
        new("input.set_action", "注入通用输入动作（含 cancel）", new { type = "object", properties = new { action = P("string"), pressed = P("boolean") } }, args => _host.InputSetAction(Arg(args, "action"), ArgBool(args, "pressed", true))),
        new("input.set_move_axis", "设置虚拟摇杆移动轴（-1..1）", new { type = "object", properties = new { x = P("number"), y = P("number") } }, args => _host.InputSetMoveAxis(ArgFloat(args, "x", 0), ArgFloat(args, "y", 0))),
        new("input.add_look_delta", "注入触摸视角增量", new { type = "object", properties = new { dx = P("number"), dy = P("number") } }, args => _host.InputAddLookDelta(ArgFloat(args, "dx", 0), ArgFloat(args, "dy", 0))),
        new("input.snapshot", "当前通用输入快照", EmptySchema(), _ => _host.InputSnapshot()),
        new("capture.screenshot", "截图（无 GPU 宿主返回错误）", new { type = "object", properties = new { path = P("string") } }, args => _host.CaptureScreenshot(Arg(args, "path"))),
        new("capture.stats", "渲染统计", EmptySchema(), _ => _host.CaptureStats()),
    ];

    private string ListTools(JsonElement id)
    {
        var capabilities = BuildCapabilities();
        return FormatResult(id, new
        {
            tools = capabilities.Tools.Select(tool => new
            {
                name = tool.Name,
                description = tool.Description,
                inputSchema = tool.InputSchema ?? new { type = "object", properties = new { } },
            }),
        });
    }

    private string ListResources(JsonElement id)
    {
        var capabilities = BuildCapabilities();
        return FormatResult(id, new
        {
            resources = capabilities.Resources.Select(resource => new
            {
                uri = resource.Uri,
                name = resource.Name,
                mimeType = resource.MimeType,
            }),
        });
    }

    private string ListPrompts(JsonElement id)
    {
        var capabilities = BuildCapabilities();
        return FormatResult(id, new
        {
            prompts = capabilities.Prompts.Select(prompt => new
            {
                name = prompt.Name,
                description = prompt.Description,
                arguments = prompt.Arguments,
            }),
        });
    }

    private McpCapabilities BuildCapabilities()
    {
        var extensions = _host as IMcpExtensionProvider;
        var tools = BuildTools().Concat(extensions?.GetTools() ?? []).ToArray();
        var resources = BuildResources().Concat(extensions?.GetResources() ?? []).ToArray();
        var prompts = BuildPrompts().Concat(extensions?.GetPrompts() ?? []).ToArray();
        EnsureUnique(tools.Select(item => item.Name), "工具");
        EnsureUnique(resources.Select(item => item.Uri), "资源 URI");
        EnsureUnique(prompts.Select(item => item.Name), "提示词");
        return new McpCapabilities(tools, resources, prompts);
    }

    private IEnumerable<McpResourceDescriptor> BuildResources()
    {
        yield return new("cubit://scene/tree", "场景树", "application/json", _host.DescribeScene);
        yield return new("cubit://run/status", "运行状态", "application/json", _host.RunStatus);
    }

    private static IEnumerable<McpPromptDescriptor> BuildPrompts()
    {
        yield return new("scene-info", "查看当前场景与运行状态", Array.Empty<object>(), _ => "请调用 scene.get_tree 查看场景树，并用 run.status 查看运行状态。");
    }

    private static string CreateProject(JsonElement args)
    {
        var path = Arg(args, "path");
        var name = Arg(args, "name");
        var project = ProjectIO.Create(path, string.IsNullOrWhiteSpace(name) ? null : name);
        return JsonSerializer.Serialize(new { projectRoot = project.RootDirectory, manifestPath = project.ManifestPath, mainScenePath = project.MainScenePath });
    }

    private static object EmptySchema() => new { type = "object", properties = new { } };

    private static object Schema(params string[] required) => new
    {
        type = "object",
        properties = required.ToDictionary(k => k, _ => new { type = "string" }),
        required,
    };

    private static object P(string type) => new { type };

    private static string Arg(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) ? value.ToString() : "";

    private static float ArgFloat(JsonElement args, string key, float fallback) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) && value.TryGetSingle(out var result) ? result : fallback;

    private static bool ArgBool(JsonElement args, string key, bool fallback) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value)
            ? value.ValueKind == JsonValueKind.True ? true : value.ValueKind == JsonValueKind.False ? false : fallback
            : fallback;

    private string FormatResult(JsonElement id, object result) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });

    private static string FormatError(JsonElement? id, int code, string message) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } });

    private static void EnsureUnique(IEnumerable<string> names, string kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!seen.Add(name))
            {
                throw new InvalidOperationException($"MCP {kind}重复: {name}");
            }
        }
    }

    private sealed record McpCapabilities(
        IReadOnlyList<McpToolDescriptor> Tools,
        IReadOnlyList<McpResourceDescriptor> Resources,
        IReadOnlyList<McpPromptDescriptor> Prompts);
}
