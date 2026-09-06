namespace Cubit.Core.Project;

/// <summary>项目清单中的作者场景引用；UID 由 ProjectMetadata 管理，不写入清单。</summary>
public sealed class CubitProjectSceneReference
{
    public string Id { get; init; } = "";

    public string Path { get; init; } = "";

    /// <summary>作者场景分类：flow、ui、gameplay、entities、audio 或 other。</summary>
    public string Category { get; init; } = "other";
}
