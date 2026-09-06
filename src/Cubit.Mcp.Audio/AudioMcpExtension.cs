using System.Text.Json;
using Cubit.Audio;
using Cubit.Core.Scene;
using Cubit.Mcp;

namespace Cubit.Mcp.Audio;

/// <summary>
/// Audio 官方模块的独立 MCP 适配器。
/// 只桥接宿主已创建的 AudioServer，不会打开设备、创建播放器或加载资源。
/// </summary>
public sealed class AudioMcpExtension : IMcpExtensionProvider
{
    private readonly SceneTree _tree;
    private readonly Func<AudioServer?> _audioServerResolver;

    public AudioMcpExtension(SceneTree tree, AudioServer? audioServer)
        : this(tree, () => audioServer)
    {
    }

    /// <summary>通过宿主运行服务解析器延迟获取 AudioServer，适配插件启停生命周期。</summary>
    public AudioMcpExtension(SceneTree tree, Func<AudioServer?> audioServerResolver)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _audioServerResolver = audioServerResolver ?? throw new ArgumentNullException(nameof(audioServerResolver));
    }

    public IEnumerable<McpToolDescriptor> GetTools()
    {
        if (CurrentAudioServer is null)
        {
            yield break;
        }

        yield return new McpToolDescriptor(
            "audio.status",
            "读取已注入 AudioServer 的状态与诊断",
            EmptySchema(),
            _ => AudioStatus());
        yield return new McpToolDescriptor(
            "audio.list_buses",
            "列出已注入 AudioServer 的总线状态",
            EmptySchema(),
            _ => ListAudioBuses());
        yield return new McpToolDescriptor(
            "audio.set_bus_volume",
            "设置已存在音频总线的音量",
            new { type = "object", properties = new { name = Property("string"), volume = Property("number") }, required = new[] { "name", "volume" } },
            SetAudioBusVolume);
        yield return new McpToolDescriptor(
            "audio.set_bus_muted",
            "设置已存在音频总线的静音状态",
            new { type = "object", properties = new { name = Property("string"), muted = Property("boolean") }, required = new[] { "name", "muted" } },
            SetAudioBusMuted);
        yield return new McpToolDescriptor(
            "audio.list_players",
            "列出场景中已存在的 AudioPlayer 与 AudioPlayer3D",
            EmptySchema(),
            _ => ListAudioPlayers());
        yield return new McpToolDescriptor(
            "audio.play_player",
            "播放已存在且已配置 AudioPlayer 或 AudioPlayer3D",
            RequiredSchema("nodePath"),
            arguments => PlayAudioPlayer(Argument(arguments, "nodePath")));
        yield return new McpToolDescriptor(
            "audio.stop_player",
            "停止已存在的 AudioPlayer 或 AudioPlayer3D",
            RequiredSchema("nodePath"),
            arguments => StopAudioPlayer(Argument(arguments, "nodePath")));
    }

    public IEnumerable<McpResourceDescriptor> GetResources()
    {
        if (CurrentAudioServer is not null)
        {
            yield return new McpResourceDescriptor(
                "cubit://audio/status",
                "音频模块状态",
                "application/json",
                AudioStatus);
        }
    }

    public IEnumerable<McpPromptDescriptor> GetPrompts()
    {
        if (CurrentAudioServer is not null)
        {
            yield return new McpPromptDescriptor(
                "audio-status",
                "检查当前项目音频服务状态",
                Array.Empty<object>(),
                _ => "请调用 audio.status 和 audio.list_buses；如需查看场景播放器，再调用 audio.list_players。");
        }
    }

    private string AudioStatus()
    {
        var status = RequireAudioServer().Status;
        return JsonSerializer.Serialize(new
        {
            hasOutputBackend = status.HasOutputBackend,
            activeVoiceCount = status.ActiveVoiceCount,
            diagnostics = status.Diagnostics.Select(diagnostic => new
            {
                code = diagnostic.Code,
                severity = diagnostic.Severity.ToString(),
                message = diagnostic.Message,
                sourcePath = diagnostic.SourcePath,
                nodePath = diagnostic.NodePath,
            }),
        });
    }

    private string ListAudioBuses() => JsonSerializer.Serialize(RequireAudioServer().Buses
        .Select(bus => new { name = bus.Name, volume = bus.Volume, muted = bus.Muted }));

    private string SetAudioBusVolume(JsonElement arguments)
    {
        RequireAudioServer().SetBusVolume(Argument(arguments, "name"), Number(arguments, "volume", 1f));
        return "ok";
    }

    private string SetAudioBusMuted(JsonElement arguments)
    {
        RequireAudioServer().SetBusMuted(Argument(arguments, "name"), Boolean(arguments, "muted", true));
        return "ok";
    }

    private string ListAudioPlayers()
    {
        var players = new List<object>();
        players.AddRange(FindNodes<AudioPlayer>().Select(player => (object)new
        {
            nodePath = player.GetPath().Value,
            type = nameof(AudioPlayer),
            bus = player.BusName,
            loop = player.Loop,
            isPlaying = player.IsPlaying,
            hasClip = player.Clip is not null,
        }));
        players.AddRange(FindNodes<AudioPlayer3D>().Select(player => (object)new
        {
            nodePath = player.GetPath().Value,
            type = nameof(AudioPlayer3D),
            bus = player.BusName,
            loop = player.Loop,
            isPlaying = player.IsPlaying,
            hasClip = player.Clip is not null,
        }));
        return JsonSerializer.Serialize(players);
    }

    private string PlayAudioPlayer(string nodePath)
    {
        switch (RequireNode(nodePath))
        {
            case AudioPlayer player:
                player.Play();
                return "ok";
            case AudioPlayer3D player3D:
                player3D.Play();
                return "ok";
            default:
                throw new InvalidOperationException($"节点不是 AudioPlayer 或 AudioPlayer3D: {nodePath}");
        }
    }

    private string StopAudioPlayer(string nodePath)
    {
        switch (RequireNode(nodePath))
        {
            case AudioPlayer player:
                player.Stop();
                return "ok";
            case AudioPlayer3D player3D:
                player3D.Stop();
                return "ok";
            default:
                throw new InvalidOperationException($"节点不是 AudioPlayer 或 AudioPlayer3D: {nodePath}");
        }
    }

    private AudioServer? CurrentAudioServer => _audioServerResolver();

    private AudioServer RequireAudioServer() => CurrentAudioServer
        ?? throw new InvalidOperationException("宿主未注入 AudioServer");

    private Node RequireNode(string nodePath)
    {
        if (string.IsNullOrWhiteSpace(nodePath))
        {
            throw new ArgumentException("节点路径不能为空", nameof(nodePath));
        }

        return _tree.Root.GetNode(new NodePath(nodePath))
            ?? throw new InvalidOperationException($"节点不存在: {nodePath}");
    }

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

    private static object EmptySchema() => new { type = "object", properties = new { } };

    private static object RequiredSchema(params string[] required) => new
    {
        type = "object",
        properties = required.ToDictionary(name => name, _ => Property("string")),
        required,
    };

    private static object Property(string type) => new { type };

    private static string Argument(JsonElement arguments, string name, string fallback = "") =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value)
            ? value.GetString() ?? fallback
            : fallback;

    private static float Number(JsonElement arguments, string name, float fallback) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.TryGetSingle(out var result)
            ? result
            : fallback;

    private static bool Boolean(JsonElement arguments, string name, bool fallback) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.True ? true : value.ValueKind == JsonValueKind.False ? false : fallback
            : fallback;
}
