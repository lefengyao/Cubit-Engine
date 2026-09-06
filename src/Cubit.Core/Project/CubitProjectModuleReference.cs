namespace Cubit.Core.Project;

/// <summary>项目模块清单项；模块只描述依赖和边界，不自动加载程序集。</summary>
public sealed class CubitProjectModuleReference
{
    public string Id { get; init; } = "";

    public string Version { get; init; } = "";

    /// <summary>feature、content、presentation 或 tool。</summary>
    public string Kind { get; init; } = "feature";

    public List<string> Dependencies { get; init; } = [];

    public List<string> Conflicts { get; init; } = [];
}
