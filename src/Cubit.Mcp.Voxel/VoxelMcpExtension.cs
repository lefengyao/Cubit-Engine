using System.Numerics;
using System.Text.Json;
using Cubit.Core.Scene;
using Cubit.Mcp;
using Cubit.Voxel.Input;
using Cubit.Voxel.World;

namespace Cubit.Mcp.Voxel;

/// <summary>按项目清单装配 Voxel MCP adapter，不创建世界、玩家或输入服务。</summary>
public sealed class VoxelMcpProjectExtensionModule : IMcpProjectExtensionModule
{
    public string PluginId => "cubit.voxel";

    public string ExpectedAssemblyName => "Cubit.Mcp.Voxel";

    public bool AlwaysAvailable => false;

    public IMcpExtensionProvider Create(McpProjectExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new VoxelMcpExtension(
            context.Tree,
            () => context.TryGetRuntimeService<VoxelInputState>(out var input) ? input : null);
    }
}

/// <summary>
/// 体素官方插件的 MCP 适配器。
/// 只桥接作者场景已有的 VoxelWorldNode 与宿主已注入的 VoxelInputState。
/// </summary>
public sealed class VoxelMcpExtension : IMcpExtensionProvider
{
    private readonly SceneTree _tree;
    private readonly Func<VoxelInputState?> _voxelInputResolver;

    public VoxelMcpExtension(SceneTree tree, Func<VoxelInputState?> voxelInputResolver)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _voxelInputResolver = voxelInputResolver ?? throw new ArgumentNullException(nameof(voxelInputResolver));
    }

    public IEnumerable<McpToolDescriptor> GetTools()
    {
        if (CurrentWorldNode is null)
        {
            yield break;
        }

        yield return new McpToolDescriptor(
            "world.get_block",
            "读取作者场景中 VoxelWorldNode 的方块（x,y,z）",
            CoordinatesSchema(),
            arguments => GetBlock(
                Integer(arguments, "x", 0),
                Integer(arguments, "y", 0),
                Integer(arguments, "z", 0)));
        yield return new McpToolDescriptor(
            "world.set_block",
            "修改作者场景中 VoxelWorldNode 的方块（x,y,z,block）",
            new
            {
                type = "object",
                properties = new { x = Property("integer"), y = Property("integer"), z = Property("integer"), block = Property("string") },
                required = new[] { "x", "y", "z", "block" },
            },
            arguments => SetBlock(
                Integer(arguments, "x", 0),
                Integer(arguments, "y", 0),
                Integer(arguments, "z", 0),
                Argument(arguments, "block")));
        yield return new McpToolDescriptor(
            "world.raycast",
            "对作者场景中 VoxelWorldNode 执行体素射线求交",
            new
            {
                type = "object",
                properties = new
                {
                    ox = Property("number"), oy = Property("number"), oz = Property("number"),
                    dx = Property("number"), dy = Property("number"), dz = Property("number"), maxDistance = Property("number"),
                },
                required = new[] { "ox", "oy", "oz", "dx", "dy", "dz", "maxDistance" },
            },
            arguments => Raycast(
                Number(arguments, "ox", 0f),
                Number(arguments, "oy", 0f),
                Number(arguments, "oz", 0f),
                Number(arguments, "dx", 0f),
                Number(arguments, "dy", 0f),
                Number(arguments, "dz", 0f),
                Number(arguments, "maxDistance", 8f)));
        yield return new McpToolDescriptor(
            "world.get_chunk_status",
            "读取作者场景中 VoxelWorldNode 的区块状态",
            new
            {
                type = "object",
                properties = new { cx = Property("integer"), cz = Property("integer") },
                required = new[] { "cx", "cz" },
            },
            arguments => GetChunkStatus(Integer(arguments, "cx", 0), Integer(arguments, "cz", 0)));
        yield return new McpToolDescriptor(
            "world.list_registry",
            "列出体素方块注册表",
            EmptySchema(),
            _ => ListRegistry());

        if (CurrentVoxelInput is null)
        {
            yield break;
        }

        yield return new McpToolDescriptor(
            "input.set_break",
            "设置体素挖掘按下/抬起状态",
            RequiredBooleanSchema("pressed"),
            arguments => SetBreak(Boolean(arguments, "pressed", true)));
        yield return new McpToolDescriptor(
            "input.set_place",
            "设置体素放置按下/抬起状态",
            RequiredBooleanSchema("pressed"),
            arguments => SetPlace(Boolean(arguments, "pressed", true)));
        yield return new McpToolDescriptor(
            "input.set_selected_block",
            "选择体素方块（名称或 ID）",
            RequiredStringSchema("block"),
            arguments => SetSelectedBlock(Argument(arguments, "block")));
    }

    public IEnumerable<McpResourceDescriptor> GetResources()
    {
        if (CurrentWorldNode is not null)
        {
            yield return new McpResourceDescriptor(
                "cubit://world/registry",
                "方块注册表",
                "application/json",
                ListRegistry);
        }
    }

    public IEnumerable<McpPromptDescriptor> GetPrompts()
    {
        if (CurrentWorldNode is null)
        {
            yield break;
        }

        yield return new McpPromptDescriptor(
            "world-flat",
            "生成平坦世界",
            new[] { new { name = "seed", description = "世界种子", required = false } },
            _ => "请调用 run.command 生成平坦世界；如需指定种子，先通过 scene.set_property 设置 VoxelWorldNode 的 Seed。");
        yield return new McpPromptDescriptor(
            "world-noise",
            "生成噪声世界",
            new[] { new { name = "seed", description = "世界种子", required = false } },
            _ => "请调用 run.command 生成噪声世界（默认种子 20260808）。");
        yield return new McpPromptDescriptor(
            "query-block",
            "查询指定方块",
            new[]
            {
                new { name = "x", description = "X 坐标", required = true },
                new { name = "y", description = "Y 坐标", required = true },
                new { name = "z", description = "Z 坐标", required = true },
            },
            arguments => $"请调用 world.get_block 查询方块 (x={Integer(arguments, "x", 0)}, y={Integer(arguments, "y", 0)}, z={Integer(arguments, "z", 0)})。");
    }

    private string GetBlock(int x, int y, int z)
    {
        var world = RequireWorld();
        var state = world.World;
        if (state is null)
        {
            return "世界未初始化";
        }

        var block = state.GetBlock(x, y, z);
        return $"{BlockRegistry.GetName(block.Id)} (id={block.Id}, data={block.Data})";
    }

    private string SetBlock(int x, int y, int z, string block)
    {
        var world = RequireWorld();
        if (world.World is null)
        {
            return "世界未初始化";
        }

        var id = BlockRegistry.FindId(block);
        if (id is null)
        {
            return $"未知方块: {block}";
        }

        return world.TrySetBlock(x, y, z, BlockRegistry.GetState(id.Value))
            ? $"ok: ({x},{y},{z}) = {BlockRegistry.GetName(id.Value)}"
            : "失败：区块未加载";
    }

    private string Raycast(float ox, float oy, float oz, float dx, float dy, float dz, float maxDistance)
    {
        var state = RequireWorld().World;
        if (state is null)
        {
            return "世界未初始化";
        }

        var hit = state.Raycast(new Vector3(ox, oy, oz), new Vector3(dx, dy, dz), maxDistance);
        if (hit is null)
        {
            return "未命中";
        }

        return $"hit=({hit.Value.Hit.X},{hit.Value.Hit.Y},{hit.Value.Hit.Z}) " +
            $"place=({hit.Value.Place.X},{hit.Value.Place.Y},{hit.Value.Place.Z}) " +
            $"block={BlockRegistry.GetName(state.GetBlock(hit.Value.Hit.X, hit.Value.Hit.Y, hit.Value.Hit.Z).Id)}";
    }

    private string GetChunkStatus(int chunkX, int chunkZ)
    {
        var state = RequireWorld().World;
        return state is null
            ? "世界未初始化"
            : $"{state.Store.GetStatus(chunkX, chunkZ)} ticket={state.Store.GetTicketLevel(chunkX, chunkZ)}";
    }

    private static string ListRegistry()
    {
        var names = new List<string>();
        for (ushort id = 0; id < 256; id++)
        {
            try
            {
                names.Add($"{id}:{BlockRegistry.GetName(id)}");
            }
            catch
            {
                break;
            }
        }

        return JsonSerializer.Serialize(names);
    }

    private string SetBreak(bool pressed)
    {
        RequireVoxelInput().SetBreak(pressed);
        return "ok";
    }

    private string SetPlace(bool pressed)
    {
        RequireVoxelInput().SetPlace(pressed);
        return "ok";
    }

    private string SetSelectedBlock(string block)
    {
        var id = BlockRegistry.FindId(block);
        if (id is null)
        {
            return $"未知方块: {block}";
        }

        RequireVoxelInput().SetSelectedBlock(id.Value);
        return "ok";
    }

    private VoxelWorldNode? CurrentWorldNode => FindNodes<VoxelWorldNode>().FirstOrDefault();

    private VoxelInputState? CurrentVoxelInput => _voxelInputResolver();

    private VoxelWorldNode RequireWorld() => CurrentWorldNode
        ?? throw new InvalidOperationException("作者场景中不存在 VoxelWorldNode");

    private VoxelInputState RequireVoxelInput() => CurrentVoxelInput
        ?? throw new InvalidOperationException("宿主未注入 VoxelInputState");

    private IEnumerable<T> FindNodes<T>() where T : Node => FindNodes<T>(_tree.Root);

    private static IEnumerable<T> FindNodes<T>(Node node) where T : Node
    {
        if (node is T match)
        {
            yield return match;
        }

        foreach (var child in node.Children)
        {
            foreach (var nestedMatch in FindNodes<T>(child))
            {
                yield return nestedMatch;
            }
        }
    }

    private static object EmptySchema() => new { type = "object", properties = new { } };

    private static object CoordinatesSchema() => new
    {
        type = "object",
        properties = new { x = Property("integer"), y = Property("integer"), z = Property("integer") },
        required = new[] { "x", "y", "z" },
    };

    private static object RequiredBooleanSchema(string name) => new
    {
        type = "object",
        properties = new Dictionary<string, object> { [name] = Property("boolean") },
        required = new[] { name },
    };

    private static object RequiredStringSchema(string name) => new
    {
        type = "object",
        properties = new Dictionary<string, object> { [name] = Property("string") },
        required = new[] { name },
    };

    private static object Property(string type) => new { type };

    private static string Argument(JsonElement arguments, string name, string fallback = "") =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value)
            ? value.GetString() ?? fallback
            : fallback;

    private static int Integer(JsonElement arguments, string name, int fallback) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
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
