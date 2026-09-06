namespace Cubit.Core.Plugins;

using Cubit.Core.Ecs;
using Cubit.Core.Scene;

/// <summary>插件向引擎声明类型与扩展的注册入口。</summary>
public sealed class PluginRegistry
{
    private sealed record PendingRegistration(
        PluginRegistration Metadata,
        Func<object>? Factory,
        PluginEditorExtensionRegistration? EditorMetadata);

    private sealed class RegistryState
    {
        public List<PendingRegistration> Items { get; } = [];

        public Dictionary<string, PendingRegistration> ByKey { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Committed { get; set; }
    }

    private readonly RegistryState _state;
    private readonly string? _pluginId;
    private readonly SceneRegistry? _sceneRegistry;

    internal PluginRegistry(SceneRegistry sceneRegistry)
        : this(new RegistryState(), null, sceneRegistry)
    {
    }

    private PluginRegistry(RegistryState state, string? pluginId, SceneRegistry? sceneRegistry)
    {
        _state = state;
        _pluginId = pluginId;
        _sceneRegistry = sceneRegistry;
    }

    public IReadOnlyList<PluginRegistration> Registrations =>
        _state.Items.Select(item => item.Metadata).ToArray();

    /// <summary>当前会话中已登记的 Dock、Inspector provider 和视口工具元数据。</summary>
    public IReadOnlyList<PluginEditorExtensionRegistration> EditorExtensions =>
        _state.Items
            .Where(item => item.EditorMetadata is not null)
            .Select(item => item.EditorMetadata!)
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Order)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>按扩展种类读取当前会话的编辑器扩展。</summary>
    public IReadOnlyList<PluginEditorExtensionRegistration> GetEditorExtensions(PluginRegistrationKind kind) =>
        EditorExtensions.Where(item => item.Kind == kind).ToArray();

    /// <summary>按节点类型选择排序最优的 Inspector provider；无目标 provider 作为兜底。</summary>
    public PluginEditorExtensionRegistration? FindInspectorProvider(Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        var fullName = targetType.FullName;
        var shortName = targetType.Name;
        return GetEditorExtensions(PluginRegistrationKind.InspectorProvider)
            .Where(item => item.TargetTypeName is null ||
                string.Equals(item.TargetTypeName, fullName, StringComparison.Ordinal) ||
                string.Equals(item.TargetTypeName, shortName, StringComparison.Ordinal))
            .OrderBy(item => item.TargetTypeName is null ? 1 : 0)
            .ThenBy(item => item.Order)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>按会话 registry 创建一个已登记的编辑器扩展实例。</summary>
    public object CreateEditorExtension(PluginRegistrationKind kind, string id)
    {
        if (kind is not (PluginRegistrationKind.EditorDock or PluginRegistrationKind.InspectorProvider or PluginRegistrationKind.ViewportTool))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "不是编辑器扩展注册类型");
        }

        var normalizedId = string.IsNullOrWhiteSpace(id)
            ? throw new ArgumentException("编辑器扩展 ID 不能为空", nameof(id))
            : id.Trim();
        var item = _state.Items.FirstOrDefault(candidate =>
            candidate.EditorMetadata is not null &&
            candidate.EditorMetadata.Kind == kind &&
            candidate.EditorMetadata.Id.Equals(normalizedId, StringComparison.OrdinalIgnoreCase));
        if (item is null || item.Factory is null)
        {
            throw new InvalidOperationException($"未找到编辑器扩展: {kind} '{normalizedId}'");
        }

        return item.Factory();
    }

    public void RegisterNode<T>(Func<T>? factory = null, string? typeName = null) where T : Node
        => Register(
            PluginRegistrationKind.Node,
            typeof(T),
            typeName,
            factory is null ? CreateFactory<T>() : () => factory());

    public void RegisterResource<T>(Func<T>? factory = null, string? typeName = null) where T : Resource
        => Register(
            PluginRegistrationKind.Resource,
            typeof(T),
            typeName,
            factory is null ? CreateFactory<T>() : () => factory());

    public void RegisterComponent<T>(string? typeName = null) where T : struct
        => Register(PluginRegistrationKind.Component, typeof(T), typeName, null);

    public void RegisterSystem<T>(Func<T>? factory = null, string? typeName = null) where T : class
    {
        if (!typeof(IEcsSystem).IsAssignableFrom(typeof(T)))
        {
            throw new InvalidOperationException(
                $"插件 {_pluginId ?? "<unscoped>"} 注册的 ECS System 必须实现 {typeof(IEcsSystem).FullName}: {typeof(T).FullName}");
        }

        Register(
            PluginRegistrationKind.System,
            typeof(T),
            typeName,
            factory is null ? CreateFactory<T>() : () => factory());
    }

    /// <summary>登记一个由 Editor 层解释和绘制的插件 Dock。</summary>
    public void RegisterEditorDock<T>(string id, string? title = null, int order = 0, Func<T>? factory = null)
        where T : class
        => RegisterEditorExtension(
            PluginRegistrationKind.EditorDock,
            typeof(T),
            id,
            title,
            order,
            null,
            factory is null ? CreateFactory<T>() : () => factory());

    /// <summary>登记一个按目标类型筛选的 Inspector provider。</summary>
    public void RegisterInspectorProvider<T>(
        string id,
        string? title = null,
        string? targetTypeName = null,
        int order = 0,
        Func<T>? factory = null)
        where T : class
        => RegisterEditorExtension(
            PluginRegistrationKind.InspectorProvider,
            typeof(T),
            id,
            title,
            order,
            targetTypeName,
            factory is null ? CreateFactory<T>() : () => factory());

    /// <summary>登记一个由中央视口宿主解释的插件工具。</summary>
    public void RegisterViewportTool<T>(string id, string? title = null, int order = 0, Func<T>? factory = null)
        where T : class
        => RegisterEditorExtension(
            PluginRegistrationKind.ViewportTool,
            typeof(T),
            id,
            title,
            order,
            null,
            factory is null ? CreateFactory<T>() : () => factory());

    internal PluginRegistry ForPlugin(string pluginId) => new(_state, pluginId, _sceneRegistry);

    internal void Commit()
    {
        if (_state.Committed)
        {
            throw new InvalidOperationException("插件注册表已经提交");
        }

        var sceneRegistry = _sceneRegistry
            ?? throw new InvalidOperationException("插件提交必须绑定显式 SceneRegistry");

        foreach (var item in _state.Items)
        {
            if (item.Metadata.Kind is PluginRegistrationKind.Node or PluginRegistrationKind.Resource)
            {
                if (item.Metadata.Kind == PluginRegistrationKind.Node)
                {
                    sceneRegistry.RegisterNode(
                        item.Metadata.Type,
                        () => (Node)item.Factory!(),
                        item.Metadata.Name);
                }
                else if (item.Metadata.Kind == PluginRegistrationKind.Resource)
                {
                    sceneRegistry.RegisterResource(
                        item.Metadata.Type,
                        () => (Resource)item.Factory!(),
                        item.Metadata.Name);
                }
            }
        }

        _state.Committed = true;
    }

    /// <summary>丢弃尚未提交到当前会话 registry 的插件注册，用于配置失败后的干净重试。</summary>
    internal void Rollback()
    {
        if (_state.Committed)
        {
            throw new InvalidOperationException("插件注册表已经提交，不能回滚");
        }

        _state.Items.Clear();
        _state.ByKey.Clear();
    }

    private void Register(
        PluginRegistrationKind kind,
        Type type,
        string? typeName,
        Func<object>? factory)
    {
        if (_pluginId is null)
        {
            throw new InvalidOperationException("只能通过插件作用域注册扩展");
        }

        if (_state.Committed)
        {
            throw new InvalidOperationException($"插件 {_pluginId} 不能在注册阶段结束后新增扩展");
        }

        var name = string.IsNullOrWhiteSpace(typeName) ? type.Name : typeName.Trim();
        var key = $"{kind}\0{name}";
        var registration = new PendingRegistration(
            new PluginRegistration(_pluginId, kind, name, type),
            factory,
            null);

        if (_state.ByKey.TryGetValue(key, out var existing))
        {
            if (existing.Metadata.PluginId.Equals(_pluginId, StringComparison.OrdinalIgnoreCase) &&
                existing.Metadata.Type == type)
            {
                return;
            }

            throw new InvalidOperationException(
                $"插件类型注册冲突: {_pluginId} 与 {existing.Metadata.PluginId} 同时注册 {kind} '{name}'");
        }

        _state.ByKey.Add(key, registration);
        _state.Items.Add(registration);
    }

    private void RegisterEditorExtension(
        PluginRegistrationKind kind,
        Type type,
        string id,
        string? title,
        int order,
        string? targetTypeName,
        Func<object>? factory)
    {
        if (_pluginId is null)
        {
            throw new InvalidOperationException("只能通过插件作用域注册扩展");
        }

        if (kind is not (PluginRegistrationKind.EditorDock or PluginRegistrationKind.InspectorProvider or PluginRegistrationKind.ViewportTool))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "不是编辑器扩展注册类型");
        }

        if (_state.Committed)
        {
            throw new InvalidOperationException($"插件 {_pluginId} 不能在注册阶段结束后新增扩展");
        }

        var normalizedId = string.IsNullOrWhiteSpace(id)
            ? throw new InvalidOperationException($"插件 {_pluginId} 的编辑器扩展 ID 不能为空")
            : id.Trim();
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? normalizedId : title.Trim();
        var key = $"{kind}\0{normalizedId}";
        var editorMetadata = new PluginEditorExtensionRegistration(
            _pluginId,
            kind,
            normalizedId,
            normalizedTitle,
            order,
            string.IsNullOrWhiteSpace(targetTypeName) ? null : targetTypeName.Trim(),
            type);
        var registration = new PendingRegistration(
            new PluginRegistration(_pluginId, kind, normalizedId, type),
            factory,
            editorMetadata);

        if (_state.ByKey.TryGetValue(key, out var existing))
        {
            if (existing.Metadata.PluginId.Equals(_pluginId, StringComparison.OrdinalIgnoreCase) &&
                existing.Metadata.Type == type)
            {
                return;
            }

            throw new InvalidOperationException(
                $"插件编辑器扩展注册冲突: {_pluginId} 与 {existing.Metadata.PluginId} 同时注册 {kind} '{normalizedId}'");
        }

        _state.ByKey.Add(key, registration);
        _state.Items.Add(registration);
    }

    private static Func<object> CreateFactory<T>() where T : class =>
        () => Activator.CreateInstance(typeof(T), nonPublic: true)
            ?? throw new InvalidOperationException($"无法实例化插件类型: {typeof(T).FullName}");
}
