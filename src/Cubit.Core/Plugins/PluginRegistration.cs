namespace Cubit.Core.Plugins;

public enum PluginRegistrationKind
{
    Node,
    Resource,
    Component,
    System,
    EditorDock,
    InspectorProvider,
    ViewportTool,
}

/// <summary>经过验证的插件扩展声明。</summary>
public sealed record PluginRegistration(
    string PluginId,
    PluginRegistrationKind Kind,
    string Name,
    Type Type);

/// <summary>编辑器扩展的通用元数据；不携带 ImGui 或其他编辑器 UI 依赖。</summary>
public sealed record PluginEditorExtensionRegistration(
    string PluginId,
    PluginRegistrationKind Kind,
    string Id,
    string Title,
    int Order,
    string? TargetTypeName,
    Type Type);
