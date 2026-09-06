namespace Cubit.Core.Project;

/// <summary>项目运行时引导器的显式程序集与类型声明。</summary>
public sealed class CubitProjectRuntimeReference
{
    /// <summary>已由平台宿主静态链接的项目程序集简单名称。</summary>
    public string Assembly { get; init; } = "";

    /// <summary>实现 <see cref="IProjectRuntimeBootstrap"/> 的完整类型名。</summary>
    public string Type { get; init; } = "";
}
