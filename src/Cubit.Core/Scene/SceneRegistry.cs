namespace Cubit.Core.Scene;

/// <summary>
/// 项目运行或编辑会话自己的节点类型注册表。
/// 注册表不扫描 AppDomain，也不与其他会话共享项目类型。
/// </summary>
public sealed class SceneRegistry
{
    private sealed record NodeEntry(Type Type, Func<Node> Factory);
    private sealed record ResourceEntry(Type Type, Func<Resource> Factory);

    private readonly Dictionary<string, NodeEntry> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResourceEntry> _resources = new(StringComparer.OrdinalIgnoreCase);

    public SceneRegistry()
    {
        RegisterNode<Node>(() => new Node());
        RegisterNode<Node3D>(() => new Node3D());
        RegisterNode<Camera3D>(() => new Camera3D());
        RegisterNode<BoxMesh3D>(() => new BoxMesh3D());
        RegisterNode<MeshInstance3D>(() => new MeshInstance3D());
        RegisterNode<EcsNode>(() => new EcsNode());
        RegisterNode<SceneInstance>(() => new SceneInstance());
        RegisterResource<Resource>(() => new Resource());
    }

    /// <summary>注册节点短名和其 CLR 全名；同一别名只能绑定同一 CLR 类型。</summary>
    public void RegisterNode<T>(Func<T>? factory = null, string? typeName = null) where T : Node
    {
        var type = typeof(T);
        Func<T> create = factory ?? (() => Activator.CreateInstance(type) as T
            ?? throw new InvalidOperationException($"无法实例化节点类型: {type.FullName}"));
        Func<Node> nodeFactory = () => create();
        RegisterNode(type, nodeFactory, typeName);
    }

    /// <summary>供插件和项目 bootstrap 注册已明确拥有的节点类型。</summary>
    public void RegisterNode(Type type, Func<Node> factory, string? typeName = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(factory);
        if (!typeof(Node).IsAssignableFrom(type) || type.IsAbstract)
        {
            throw new ArgumentException($"只能注册可实例化的 Node 类型: {type.FullName}", nameof(type));
        }

        var name = string.IsNullOrWhiteSpace(typeName) ? type.Name : typeName.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("节点类型名不能为空", nameof(typeName));
        }

        var entry = new NodeEntry(type, factory);
        EnsureAliasAvailable(name, entry);
        AddAlias(name, entry);

        var fullName = type.FullName ?? type.Name;
        EnsureAliasAvailable(fullName, entry);
        AddAlias(fullName, entry);
    }

    /// <summary>按当前会话的显式注册表实例化节点；未知类型返回 null。</summary>
    public Node? InstantiateNode(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        return _nodes.TryGetValue(typeName, out var entry) ? entry.Factory() : null;
    }

    /// <summary>解析当前会话明确注册的节点类型；未知类型返回 null。</summary>
    public Type? ResolveNodeType(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        return _nodes.TryGetValue(typeName, out var entry) ? entry.Type : null;
    }

    /// <summary>注册当前项目会话拥有的资源类型，不写入全局 ClassDB。</summary>
    public void RegisterResource<T>(Func<T>? factory = null, string? typeName = null) where T : Resource
    {
        var type = typeof(T);
        Func<T> create = factory ?? (() => Activator.CreateInstance(type) as T
            ?? throw new InvalidOperationException($"无法实例化资源类型: {type.FullName}"));
        RegisterResource(type, () => create(), typeName);
    }

    /// <summary>供插件注册当前会话明确拥有的资源类型。</summary>
    public void RegisterResource(Type type, Func<Resource> factory, string? typeName = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(factory);
        if (!typeof(Resource).IsAssignableFrom(type) || type.IsAbstract)
        {
            throw new ArgumentException($"只能注册可实例化的 Resource 类型: {type.FullName}", nameof(type));
        }

        var name = string.IsNullOrWhiteSpace(typeName) ? type.Name : typeName.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("资源类型名不能为空", nameof(typeName));
        }

        var entry = new ResourceEntry(type, factory);
        EnsureResourceAliasAvailable(name, entry);
        AddResourceAlias(name, entry);

        var fullName = type.FullName ?? type.Name;
        EnsureResourceAliasAvailable(fullName, entry);
        AddResourceAlias(fullName, entry);
    }

    /// <summary>按当前会话的显式注册表实例化资源；未知类型返回 null。</summary>
    public Resource? InstantiateResource(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        return _resources.TryGetValue(typeName, out var entry) ? entry.Factory() : null;
    }

    /// <summary>解析当前会话明确注册的资源类型；未知类型返回 null。</summary>
    public Type? ResolveResourceType(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        return _resources.TryGetValue(typeName, out var entry) ? entry.Type : null;
    }

    /// <summary>返回当前会话注册的节点短名。</summary>
    public IReadOnlyList<string> KnownNodeTypes => _nodes
        .Where(item => !item.Key.Contains('.'))
        .Select(item => item.Key)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    /// <summary>返回当前会话注册的资源短名。</summary>
    public IReadOnlyList<string> KnownResourceTypes => _resources
        .Where(item => !item.Key.Contains('.'))
        .Select(item => item.Key)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    private void EnsureAliasAvailable(string alias, NodeEntry entry)
    {
        if (_nodes.TryGetValue(alias, out var existing) && existing.Type != entry.Type)
        {
            throw new InvalidOperationException(
                $"SceneRegistry 类型名冲突: '{alias}' 已属于 {existing.Type.FullName}，不能注册 {entry.Type.FullName}");
        }
    }

    private void AddAlias(string alias, NodeEntry entry) => _nodes[alias] = entry;

    private void EnsureResourceAliasAvailable(string alias, ResourceEntry entry)
    {
        if (_resources.TryGetValue(alias, out var existing) && existing.Type != entry.Type)
        {
            throw new InvalidOperationException(
                $"SceneRegistry 资源类型名冲突: '{alias}' 已属于 {existing.Type.FullName}，不能注册 {entry.Type.FullName}");
        }
    }

    private void AddResourceAlias(string alias, ResourceEntry entry) => _resources[alias] = entry;
}
