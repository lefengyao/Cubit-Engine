using System.Numerics;
using System.Text.Json;
using Cubit.Core.Scene;
using Cubit.Mcp;
using Cubit.Physics.Scene;

namespace Cubit.Mcp.Physics;

/// <summary>按项目清单装配 Physics MCP adapter。</summary>
public sealed class PhysicsMcpProjectExtensionModule : IMcpProjectExtensionModule
{
    public string PluginId => "cubit.physics";

    public string ExpectedAssemblyName => "Cubit.Mcp.Physics";

    public bool AlwaysAvailable => false;

    public IMcpExtensionProvider Create(McpProjectExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new PhysicsMcpExtension(context.Tree);
    }
}

/// <summary>只桥接作者场景已有的物理世界，不创建世界或碰撞体。</summary>
public sealed class PhysicsMcpExtension : IMcpExtensionProvider
{
    private readonly SceneTree _tree;

    public PhysicsMcpExtension(SceneTree tree)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
    }

    public IEnumerable<McpToolDescriptor> GetTools()
    {
        if (!FindNodes<PhysicsWorld3D>().Any())
        {
            yield break;
        }

        yield return new McpToolDescriptor(
            "physics.list_worlds",
            "列出场景中已存在的 PhysicsWorld3D 状态",
            EmptySchema(),
            _ => ListPhysicsWorlds());
        yield return new McpToolDescriptor(
            "physics.raycast",
            "对已存在的 PhysicsWorld3D 执行只读射线查询",
            new
            {
                type = "object",
                properties = new
                {
                    nodePath = Property("string"), ox = Property("number"), oy = Property("number"), oz = Property("number"),
                    dx = Property("number"), dy = Property("number"), dz = Property("number"), maxDistance = Property("number"),
                    collisionMask = Property("integer"), collisionLayer = Property("integer"),
                },
                required = new[] { "nodePath", "ox", "oy", "oz", "dx", "dy", "dz", "maxDistance" },
            },
            PhysicsRaycast);
    }

    public IEnumerable<McpResourceDescriptor> GetResources() => [];

    public IEnumerable<McpPromptDescriptor> GetPrompts() => [];

    private string ListPhysicsWorlds() => JsonSerializer.Serialize(FindNodes<PhysicsWorld3D>()
        .Select(world => new
        {
            nodePath = world.GetPath().Value,
            colliderCount = world.ColliderCount,
            gravity = Vector(world.Gravity),
            diagnostics = Diagnostics(world.Diagnostics.Snapshot()),
        }));

    private string PhysicsRaycast(JsonElement arguments)
    {
        var world = RequireNode<PhysicsWorld3D>(Argument(arguments, "nodePath"));
        var origin = new Vector3(
            Number(arguments, "ox", 0f),
            Number(arguments, "oy", 0f),
            Number(arguments, "oz", 0f));
        var direction = new Vector3(
            Number(arguments, "dx", 0f),
            Number(arguments, "dy", 0f),
            Number(arguments, "dz", 0f));
        var results = new List<PhysicsRayHit3D>();
        world.Raycast(
            origin,
            direction,
            Number(arguments, "maxDistance", 0f),
            new PhysicsQueryFilter(
                UnsignedNumber(arguments, "collisionMask", uint.MaxValue),
                UnsignedNumber(arguments, "collisionLayer", uint.MaxValue)),
            results);
        return JsonSerializer.Serialize(results.Select(hit => new
        {
            colliderPath = hit.Collider.GetPath().Value,
            collisionShapePath = hit.CollisionShape.GetPath().Value,
            position = Vector(hit.Position),
            normal = Vector(hit.Normal),
            distance = hit.Distance,
            colliderId = hit.ColliderId,
        }));
    }

    private T RequireNode<T>(string nodePath) where T : Node => _tree.Root.GetNode(new NodePath(nodePath)) as T
        ?? throw new InvalidOperationException($"节点类型不匹配或不存在: {nodePath}，需要 {typeof(T).Name}");

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

    private static object Property(string type) => new { type };

    private static string Argument(JsonElement arguments, string name, string fallback = "") =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value)
            ? value.GetString() ?? fallback
            : fallback;

    private static float Number(JsonElement arguments, string name, float fallback) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.TryGetSingle(out var result)
            ? result
            : fallback;

    private static uint UnsignedNumber(JsonElement arguments, string name, uint fallback) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.TryGetUInt32(out var result)
            ? result
            : fallback;

    private static object Vector(Vector3 value) => new { x = value.X, y = value.Y, z = value.Z };

    private static IEnumerable<object> Diagnostics(IEnumerable<Cubit.Core.Diagnostics.EngineDiagnostic> diagnostics) => diagnostics.Select(diagnostic => (object)new
    {
        code = diagnostic.Code,
        severity = diagnostic.Severity.ToString(),
        message = diagnostic.Message,
        sourcePath = diagnostic.SourcePath,
        nodePath = diagnostic.NodePath,
    });
}
