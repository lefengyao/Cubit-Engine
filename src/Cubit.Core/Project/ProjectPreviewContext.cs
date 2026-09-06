using Cubit.Core.Scene;

namespace Cubit.Core.Project;

/// <summary>传给项目预览引导器的纯 Core 上下文。</summary>
public sealed class ProjectPreviewContext
{
    public ProjectPreviewContext(CubitProject project, Node sceneRoot)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        SceneRoot = sceneRoot ?? throw new ArgumentNullException(nameof(sceneRoot));
    }

    /// <summary>已验证的项目根与清单。</summary>
    public CubitProject Project { get; }

    /// <summary>尚未进入 SceneTree 的作者场景副本。</summary>
    public Node SceneRoot { get; }
}
