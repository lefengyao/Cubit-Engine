using System.Reflection;
using System.Text.Json;

namespace Cubit.Core.Scene;

/// <summary>
/// 场景 IO：把节点树保存为 JSON 场景文件（类 .tscn），并可实例化回节点树。
/// 序列化：节点类型 + 名称 + 所有 [Export] 属性 + 子节点。
/// </summary>
public static class SceneIO
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string SerializeToJson(Node root, string? uid = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var file = SerializeNode(root);
        file.Uid = uid;
        return JsonSerializer.Serialize(file, Options);
    }

    public static void SaveToJson(Node root, string path)
    {
        var json = SerializeToJson(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, json);
    }

    public static Node DeserializeFromJson(string json, Func<string, Node> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Instantiate(DeserializeFile(json), factory, registry: null);
    }

    /// <summary>使用当前项目会话的显式节点注册表反序列化场景。</summary>
    public static Node DeserializeFromJson(string json, SceneRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return Instantiate(DeserializeFile(json), factory: null, registry);
    }

    public static Node LoadFromJson(string path, Func<string, Node> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return DeserializeFromJson(File.ReadAllText(path), factory);
    }

    /// <summary>从文件读取场景，并严格使用当前项目会话的显式节点注册表。</summary>
    public static Node LoadFromJson(string path, SceneRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return DeserializeFromJson(File.ReadAllText(path), registry);
    }

    /// <summary>读取场景文件头的稳定 UID；旧场景未迁移时返回 null。</summary>
    public static string? GetDocumentUid(string json) => DeserializeFile(json).Uid;

    /// <summary>为旧场景补齐稳定 UID；若场景已有不同 UID 则拒绝覆盖。</summary>
    public static string WithDocumentUid(string json, string uid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        var file = DeserializeFile(json);
        if (!string.IsNullOrWhiteSpace(file.Uid) && !string.Equals(file.Uid, uid, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"场景 UID 与项目缓存冲突: {file.Uid}");
        }

        file.Uid = uid;
        return JsonSerializer.Serialize(file, Options);
    }

    /// <summary>检测旧序列化器遗留在子节点上的 uid 字段；子节点不是独立场景文件，不能拥有文件 UID。</summary>
    public static bool HasNestedUidProperty(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object &&
            HasNestedUidProperty(document.RootElement, isRoot: true);
    }

    private static SceneFile SerializeNode(Node node)
    {
        var properties = new Dictionary<string, JsonElement>();
        foreach (var property in GetExported(node.GetType()))
        {
            var value = property.GetValue(node);
            properties[property.Name] = JsonSerializer.SerializeToElement(value, property.PropertyType, Options);
        }

        return new SceneFile
        {
            Type = node.GetType().FullName ?? node.GetType().Name,
            Name = node.Name,
            Properties = properties.Count > 0 ? properties : null,
            Children = node is not SceneInstance && node.Children.Count > 0
                ? node.Children.Select(SerializeNode).ToList()
                : null,
        };
    }

    private static SceneFile DeserializeFile(string json) =>
        JsonSerializer.Deserialize<SceneFile>(json, Options)
        ?? throw new InvalidOperationException("场景文件解析失败");

    private static bool HasNestedUidProperty(JsonElement node, bool isRoot)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!isRoot && node.EnumerateObject().Any(property =>
                property.Name.Equals("uid", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var children = node.EnumerateObject().FirstOrDefault(property =>
            property.Name.Equals("Children", StringComparison.OrdinalIgnoreCase)).Value;
        return children.ValueKind == JsonValueKind.Array && children.EnumerateArray()
            .Any(child => HasNestedUidProperty(child, isRoot: false));
    }

    private static Node Instantiate(SceneFile file, Func<string, Node>? factory, SceneRegistry? registry)
    {
        Node? node;
        if (factory is not null)
        {
            node = factory(file.Type);
        }
        else if (registry is not null)
        {
            node = registry.InstantiateNode(file.Type)
                ?? throw new InvalidOperationException($"当前场景会话未注册节点类型: {file.Type}");
        }
        else
        {
            throw new InvalidOperationException($"场景反序列化必须提供显式 SceneRegistry 或节点工厂: {file.Type}");
        }

        node.Name = file.Name;

        foreach (var (key, element) in file.Properties ?? [])
        {
            var property = node.GetType().GetProperty(key, BindingFlags.Public | BindingFlags.Instance);
            if (property is not null && property.SetMethod is not null)
            {
                property.SetValue(node, element.Deserialize(property.PropertyType, Options));
            }
        }

        foreach (var child in file.Children ?? [])
        {
            node.AddChild(Instantiate(child, factory, registry));
        }

        return node;
    }

    private static IEnumerable<PropertyInfo> GetExported(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<ExportAttribute>() is not null);

}

