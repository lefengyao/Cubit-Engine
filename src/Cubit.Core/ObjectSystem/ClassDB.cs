using System.Reflection;
using Cubit.Core.Scene;

namespace Cubit.Core.ObjectSystem;

/// <summary>
/// 引擎类型系统（借鉴 Godot ClassDB）：统一注册引擎类型（节点/资源），
/// 提供 Core 内置类型的按名实例化与属性/方法/信号元数据枚举。
/// 项目/插件运行时类型由会话级 SceneRegistry 管理，不进入此兼容门面。
/// </summary>
public static class ClassDB
{
    private sealed class TypeEntry
    {
        public required string TypeName { get; init; }

        public required Type Type { get; init; }

        public required Func<object> Factory { get; init; }
    }

    private static readonly Dictionary<string, TypeEntry> Types = new(StringComparer.OrdinalIgnoreCase);

    static ClassDB()
    {
        // 引擎内置类型：节点与资源统一注册（短名 + 全名）
        Register<Node>(() => new Node());
        Register<Scene.Node3D>(() => new Scene.Node3D());
        Register<Scene.Camera3D>(() => new Scene.Camera3D());
        Register<Scene.MeshInstance3D>(() => new Scene.MeshInstance3D());
        Register<Scene.EcsNode>(() => new Scene.EcsNode());
        Register<Resource>(() => new Resource());
    }

    /// <summary>注册类型：typeName 缺省用类型短名；同时注册全名。工厂缺省走 Activator。</summary>
    public static void Register<T>(Func<T>? factory = null, string? typeName = null) where T : class
    {
        var type = typeof(T);
        Func<object> create = factory is null
            ? () => Activator.CreateInstance(type) as T ?? throw new InvalidOperationException($"无法实例化类型: {type.Name}")
            : () => factory()!;
        Register(type, create, typeName);
    }

    /// <summary>按运行时类型注册；插件系统用它提交已经完成冲突检查的工厂。</summary>
    public static void Register(Type type, Func<object> factory, string? typeName = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(factory);
        if (!type.IsClass || type.IsAbstract)
        {
            throw new ArgumentException($"只能注册可实例化的类: {type.FullName}", nameof(type));
        }

        var name = string.IsNullOrWhiteSpace(typeName) ? type.Name : typeName.Trim();
        var fullName = type.FullName ?? type.Name;
        ValidateRegistration(type, name);
        RegisterAlias(name, type, factory);
        RegisterAlias(fullName, type, factory);
    }

    /// <summary>按当前 Core 显式注册的类型名实例化；未知类型返回 null。</summary>
    public static object? Instantiate(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        if (Types.TryGetValue(typeName, out var entry))
        {
            return entry.Factory();
        }

        return null;
    }

    /// <summary>已知类型短名（编辑器“创建节点”菜单用）。</summary>
    public static string[] KnownTypes => Types.Values
        .Select(e => e.TypeName)
        .Where(n => !n.Contains('.'))
        .Distinct()
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToArray();

    /// <summary>属性元数据：[Export] 优先 + 可编辑简单类型，含类型与当前值（Inspector 用）。</summary>
    public static PropertyInfo[] GetProperties(string typeName)
    {
        var type = ResolveType(typeName);
        return type is null
            ? []
            : type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && (p.GetCustomAttribute<Scene.ExportAttribute>() is not null || IsEditableType(p.PropertyType)))
                .ToArray();
    }

    /// <summary>公开实例方法元数据（含参数个数；MCP scene.call_method / 编辑器调用用）。</summary>
    public static MethodInfo[] GetMethods(string typeName)
    {
        var type = ResolveType(typeName);
        return type is null
            ? []
            : type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
                .ToArray();
    }

    /// <summary>信号元数据：C# event（Godot 信号在 C# 中即 event）。</summary>
    public static EventInfo[] GetSignals(string typeName)
    {
        var type = ResolveType(typeName);
        return type is null
            ? []
            : type.GetEvents(BindingFlags.Public | BindingFlags.Instance);
    }

    /// <summary>按短名/全名解析已注册类型。</summary>
    public static Type? ResolveType(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        if (Types.TryGetValue(typeName, out var entry))
        {
            return entry.Type;
        }

        return null;
    }

    internal static void ValidateRegistration(Type type, string typeName)
    {
        EnsureAliasAvailable(typeName, type);
        EnsureAliasAvailable(type.FullName ?? type.Name, type);
    }

    private static void EnsureAliasAvailable(string alias, Type type)
    {
        if (Types.TryGetValue(alias, out var existing) && existing.Type != type && existing.Type != typeof(object))
        {
            throw new InvalidOperationException(
                $"ClassDB 类型名冲突: '{alias}' 已属于 {existing.Type.FullName}，不能注册 {type.FullName}");
        }
    }

    private static void RegisterAlias(string alias, Type type, Func<object> factory)
    {
        Types[alias] = new TypeEntry { TypeName = alias, Type = type, Factory = factory };
    }

    private static bool IsEditableType(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
        type == typeof(System.Numerics.Vector3) || type == typeof(Guid) || type == typeof(DateTime);
}
