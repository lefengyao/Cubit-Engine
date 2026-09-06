namespace Cubit.Editor.Scenes;

/// <summary>编辑器中可显式启动和停止项目运行时预览的会话。</summary>
public interface IEditorPreviewSession
{
    /// <summary>项目是否声明了可用的预览入口。</summary>
    bool CanStartPreview { get; }

    /// <summary>当前是否正运行项目预览。</summary>
    bool IsPreviewing { get; }

    /// <summary>重新加载作者场景并显式运行项目预览引导器。</summary>
    void StartPreview();

    /// <summary>停止预览并恢复从磁盘加载的作者场景。</summary>
    void StopPreview();
}
