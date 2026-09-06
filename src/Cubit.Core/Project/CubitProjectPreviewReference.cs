namespace Cubit.Core.Project;

/// <summary>项目运行时预览的显式程序集与引导器类型声明。</summary>
public sealed class CubitProjectPreviewReference
{
    /// <summary>相对项目根目录的程序集路径。</summary>
    public string Assembly { get; init; } = "";

    /// <summary>实现 <see cref="IProjectPreviewBootstrap"/> 的完整类型名。</summary>
    public string Type { get; init; } = "";
}
