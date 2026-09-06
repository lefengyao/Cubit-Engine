using Cubit.Core.Scene;

namespace Cubit.Core.Project;

/// <summary>项目编辑器扩展种类；实现位于项目程序集，绘制由 Cubit.Editor 托管。</summary>
public enum ProjectEditorExtensionKind
{
    Inspector,
    Dock,
    ViewportTool,
}

/// <summary>项目 Inspector 的语义属性分组。</summary>
public sealed record ProjectEditorPropertyGroup(string Name, IReadOnlyList<string> PropertyNames);

/// <summary>不依赖 ImGui 的项目编辑器绘制上下文。</summary>
public interface IProjectEditorUiContext
{
    void Text(string text);

    bool Button(string label);

    bool InputText(string label, ref string value, int maxLength);

    void Separator();
}

/// <summary>项目 Dock 扩展；项目只描述内容，窗口生命周期由 Editor 管理。</summary>
public interface IProjectEditorDock
{
    void Draw(Node root, IProjectEditorUiContext context);
}

/// <summary>项目节点 Inspector 扩展；通用属性控件仍由 Editor 负责。</summary>
public interface IProjectEditorInspectorProvider
{
    bool CanInspect(Node node);

    void Draw(Node node, IProjectEditorUiContext context);
}

/// <summary>项目中央视口工具扩展。</summary>
public interface IProjectEditorViewportTool
{
    void Draw(Node root, IProjectEditorUiContext context);
}

/// <summary>项目程序集的可选编辑器注册入口；编辑态只调用该入口，不启动项目服务。</summary>
public interface IProjectEditorExtensionSource
{
    void RegisterProjectEditorExtensions(CubitProject project, ProjectEditorRegistry registry);
}

/// <summary>当前项目会话的编辑器扩展 registry；不与官方插件 registry 混用。</summary>
public sealed class ProjectEditorRegistry
{
    private readonly List<ProjectEditorExtensionRegistration> _extensions = [];
    private readonly Dictionary<string, ProjectEditorExtensionRegistration> _byKey =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Type, ProjectEditorNodePresentation> _nodePresentations = [];
    private bool _committed;

    /// <summary>按稳定顺序枚举项目扩展。</summary>
    public IReadOnlyList<ProjectEditorExtensionRegistration> Extensions =>
        _extensions
            .OrderBy(extension => extension.Kind)
            .ThenBy(extension => extension.Order)
            .ThenBy(extension => extension.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>按类型读取项目节点在创建器中的显示元数据。</summary>
    public ProjectEditorNodePresentation? FindNodePresentation(Type nodeType)
    {
        ArgumentNullException.ThrowIfNull(nodeType);
        return _nodePresentations.TryGetValue(nodeType, out var presentation) ? presentation : null;
    }

    /// <summary>登记项目节点的编辑器分类和显示标题；不影响场景序列化类型名。</summary>
    public void RegisterNodeType<TNode>(string title, string group)
        where TNode : Node
    {
        if (_committed)
        {
            throw new InvalidOperationException("项目编辑器 registry 已提交，不能继续注册节点元数据");
        }

        var normalizedTitle = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("项目节点显示标题不能为空", nameof(title))
            : title.Trim();
        var normalizedGroup = string.IsNullOrWhiteSpace(group)
            ? throw new ArgumentException("项目节点分类不能为空", nameof(group))
            : group.Trim();
        var nodeType = typeof(TNode);
        if (_nodePresentations.ContainsKey(nodeType))
        {
            throw new InvalidOperationException($"项目节点编辑器元数据重复: {nodeType.FullName}");
        }

        _nodePresentations.Add(nodeType, new ProjectEditorNodePresentation(nodeType, normalizedTitle, normalizedGroup));
    }

    /// <summary>按节点类型选择最具体的项目 Inspector。</summary>
    public ProjectEditorExtensionRegistration? FindInspector(Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        return _extensions
            .Where(extension => extension.Kind == ProjectEditorExtensionKind.Inspector &&
                extension.TargetType is not null && extension.TargetType.IsAssignableFrom(targetType))
            .OrderBy(extension => extension.TargetType == targetType ? 0 : 1)
            .ThenBy(extension => extension.Order)
            .ThenBy(extension => extension.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>按类型读取项目属性分组；没有注册时返回空集合。</summary>
    public IReadOnlyList<ProjectEditorPropertyGroup> GetPropertyGroups(Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        return FindInspector(targetType)?.PropertyGroups ?? [];
    }

    /// <summary>创建当前会话中的项目扩展实例。</summary>
    public object CreateExtension(ProjectEditorExtensionKind kind, string id)
    {
        var normalizedId = NormalizeId(id);
        var extension = _extensions.FirstOrDefault(item =>
            item.Kind == kind && item.Id.Equals(normalizedId, StringComparison.OrdinalIgnoreCase));
        if (extension is null || extension.Factory is null)
        {
            throw new InvalidOperationException($"未找到项目编辑器扩展: {kind} '{normalizedId}'");
        }

        return extension.Factory();
    }

    public void RegisterInspector<TNode, TProvider>(
        string id,
        string title,
        int order = 0,
        IReadOnlyList<ProjectEditorPropertyGroup>? propertyGroups = null,
        Func<TProvider>? factory = null)
        where TNode : Node
        where TProvider : class, IProjectEditorInspectorProvider
        => Register(
            ProjectEditorExtensionKind.Inspector,
            id,
            title,
            order,
            typeof(TNode),
            propertyGroups,
            factory is null ? CreateFactory<TProvider>() : () => factory());

    public void RegisterDock<T>(
        string id,
        string title,
        int order = 0,
        Func<T>? factory = null)
        where T : class, IProjectEditorDock
        => Register(
            ProjectEditorExtensionKind.Dock,
            id,
            title,
            order,
            null,
            null,
            factory is null ? CreateFactory<T>() : () => factory());

    public void RegisterViewportTool<T>(
        string id,
        string title,
        int order = 0,
        Func<T>? factory = null)
        where T : class, IProjectEditorViewportTool
        => Register(
            ProjectEditorExtensionKind.ViewportTool,
            id,
            title,
            order,
            null,
            null,
            factory is null ? CreateFactory<T>() : () => factory());

    /// <summary>注册结束后冻结列表，防止编辑器 UI 绘制期间改变扩展集合。</summary>
    internal void Commit() => _committed = true;

    private void Register(
        ProjectEditorExtensionKind kind,
        string id,
        string title,
        int order,
        Type? targetType,
        IReadOnlyList<ProjectEditorPropertyGroup>? propertyGroups,
        Func<object> factory)
    {
        if (_committed)
        {
            throw new InvalidOperationException("项目编辑器 registry 已提交，不能继续注册扩展");
        }

        var normalizedId = NormalizeId(id);
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? normalizedId : title.Trim();
        var key = $"{kind}\0{normalizedId}";
        if (_byKey.ContainsKey(key))
        {
            throw new InvalidOperationException($"项目编辑器扩展 ID 冲突: {kind} '{normalizedId}'");
        }

        var groups = propertyGroups?.Select(group =>
        {
            ArgumentNullException.ThrowIfNull(group);
            var name = string.IsNullOrWhiteSpace(group.Name)
                ? throw new InvalidOperationException("项目 Inspector 分组名称不能为空")
                : group.Name.Trim();
            var names = group.PropertyNames
                .Where(propertyName => !string.IsNullOrWhiteSpace(propertyName))
                .Select(propertyName => propertyName.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new ProjectEditorPropertyGroup(name, names);
        }).ToArray() ?? [];

        if (targetType is not null)
        {
            var knownProperties = targetType
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            var assignedProperties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in groups)
            {
                foreach (var propertyName in group.PropertyNames)
                {
                    if (!knownProperties.Contains(propertyName))
                    {
                        throw new InvalidOperationException(
                            $"项目 Inspector '{normalizedId}' 的属性不存在: {targetType.FullName}.{propertyName}");
                    }

                    if (!assignedProperties.Add(propertyName))
                    {
                        throw new InvalidOperationException(
                            $"项目 Inspector '{normalizedId}' 的属性重复归组: {targetType.FullName}.{propertyName}");
                    }
                }
            }
        }

        var registration = new ProjectEditorExtensionRegistration(
            kind,
            normalizedId,
            normalizedTitle,
            order,
            targetType,
            groups,
            factory);
        _byKey.Add(key, registration);
        _extensions.Add(registration);
    }

    private static string NormalizeId(string id) =>
        string.IsNullOrWhiteSpace(id)
            ? throw new ArgumentException("项目编辑器扩展 ID 不能为空", nameof(id))
            : id.Trim();

    private static Func<object> CreateFactory<T>() where T : class =>
        () => Activator.CreateInstance(typeof(T), nonPublic: true)
            ?? throw new InvalidOperationException($"无法实例化项目编辑器扩展: {typeof(T).FullName}");
}

/// <summary>项目编辑器扩展的只读注册元数据。</summary>
public sealed record ProjectEditorExtensionRegistration(
    ProjectEditorExtensionKind Kind,
    string Id,
    string Title,
    int Order,
    Type? TargetType,
    IReadOnlyList<ProjectEditorPropertyGroup> PropertyGroups,
    Func<object>? Factory);

/// <summary>项目节点在 Editor 创建器中的显示标题与分类。</summary>
public sealed record ProjectEditorNodePresentation(Type NodeType, string Title, string Group);
