using Cubit.Core.Scene;

namespace Cubit.Editor.Contracts;

/// <summary>编辑器插件绘制上下文；由 Editor 映射到具体 UI 后端。</summary>
public interface IEditorUiContext
{
    void Text(string text);

    bool Button(string label);

    void Separator();
}

/// <summary>可停靠面板扩展；插件不直接控制窗口生命周期。</summary>
public interface IEditorDock
{
    void Draw(IEditorUiContext context);
}

/// <summary>节点 Inspector 扩展；返回 true 表示 provider 支持该节点。</summary>
public interface IEditorInspectorProvider
{
    bool CanInspect(Node node);

    void Draw(Node node, IEditorUiContext context);
}

/// <summary>中央视口工具扩展；插件只绘制工具内容，不拥有视口生命周期。</summary>
public interface IEditorViewportTool
{
    void Draw(IEditorUiContext context);
}
