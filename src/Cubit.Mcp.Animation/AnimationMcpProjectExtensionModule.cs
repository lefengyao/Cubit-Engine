using System.Text.Json;
using Cubit.Animation;
using Cubit.Core.Scene;
using Cubit.Mcp;

namespace Cubit.Mcp.Animation;

/// <summary>按项目清单装配 Animation MCP adapter。</summary>
public sealed class AnimationMcpProjectExtensionModule : IMcpProjectExtensionModule
{
    public string PluginId => "cubit.animation";

    public string ExpectedAssemblyName => "Cubit.Mcp.Animation";

    public bool AlwaysAvailable => false;

    public IMcpExtensionProvider Create(McpProjectExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new AnimationMcpExtension(context.Tree);
    }
}

/// <summary>只桥接作者场景已有的动画播放器，不创建片段或播放器。</summary>
public sealed class AnimationMcpExtension : IMcpExtensionProvider
{
    private readonly SceneTree _tree;

    public AnimationMcpExtension(SceneTree tree)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
    }

    public IEnumerable<McpToolDescriptor> GetTools()
    {
        if (!FindNodes<AnimationPlayer>().Any())
        {
            yield break;
        }

        yield return new McpToolDescriptor(
            "animation.list_players",
            "列出场景中已存在的 AnimationPlayer 播放状态",
            EmptySchema(),
            _ => ListAnimationPlayers());
        yield return new McpToolDescriptor(
            "animation.play",
            "播放已存在 AnimationPlayer 的已注册片段",
            new
            {
                type = "object",
                properties = new { nodePath = Property("string"), clip = Property("string"), restart = Property("boolean") },
                required = new[] { "nodePath", "clip" },
            },
            PlayAnimation);
        yield return new McpToolDescriptor(
            "animation.pause",
            "暂停已存在的 AnimationPlayer",
            RequiredSchema("nodePath"),
            arguments => PauseAnimation(RequirePlayer(Argument(arguments, "nodePath"))));
        yield return new McpToolDescriptor(
            "animation.stop",
            "停止已存在的 AnimationPlayer",
            RequiredSchema("nodePath"),
            arguments => StopAnimation(RequirePlayer(Argument(arguments, "nodePath"))));
        yield return new McpToolDescriptor(
            "animation.seek",
            "定位已存在 AnimationPlayer 的当前片段",
            new
            {
                type = "object",
                properties = new { nodePath = Property("string"), time = Property("number") },
                required = new[] { "nodePath", "time" },
            },
            SeekAnimation);
    }

    public IEnumerable<McpResourceDescriptor> GetResources() => [];

    public IEnumerable<McpPromptDescriptor> GetPrompts() => [];

    private string ListAnimationPlayers() => JsonSerializer.Serialize(FindNodes<AnimationPlayer>()
        .Select(player => new
        {
            nodePath = player.GetPath().Value,
            clips = player.ClipNames,
            currentClip = player.CurrentClipName,
            currentTime = player.CurrentTime,
            speedScale = player.SpeedScale,
            isPlaying = player.IsPlaying,
        }));

    private string PlayAnimation(JsonElement arguments)
    {
        var player = RequirePlayer(Argument(arguments, "nodePath"));
        player.Play(Argument(arguments, "clip"), Boolean(arguments, "restart", true));
        return "ok";
    }

    private static string PauseAnimation(AnimationPlayer player)
    {
        player.Pause();
        return "ok";
    }

    private static string StopAnimation(AnimationPlayer player)
    {
        player.Stop();
        return "ok";
    }

    private string SeekAnimation(JsonElement arguments)
    {
        RequirePlayer(Argument(arguments, "nodePath")).Seek(DoubleNumber(arguments, "time", 0d));
        return "ok";
    }

    private AnimationPlayer RequirePlayer(string nodePath)
    {
        if (string.IsNullOrWhiteSpace(nodePath))
        {
            throw new ArgumentException("节点路径不能为空", nameof(nodePath));
        }

        return _tree.Root.GetNode(new NodePath(nodePath)) as AnimationPlayer
            ?? throw new InvalidOperationException($"节点类型不匹配或不存在: {nodePath}，需要 {nameof(AnimationPlayer)}");
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

    private static double DoubleNumber(JsonElement arguments, string name, double fallback) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.TryGetDouble(out var result)
            ? result
            : fallback;

    private static bool Boolean(JsonElement arguments, string name, bool fallback) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.True ? true : value.ValueKind == JsonValueKind.False ? false : fallback
            : fallback;
}
