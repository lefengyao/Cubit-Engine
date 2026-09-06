namespace Cubit.Core.Project;

/// <summary>桌面宿主可选加载的项目呈现程序集与类型声明。</summary>
public sealed class CubitProjectDesktopPresentationReference
{
    /// <summary>已由桌面宿主静态链接的项目程序集简单名称。</summary>
    public string Assembly { get; init; } = "";

    /// <summary>实现桌面呈现契约的公开具体类型完整名称。</summary>
    public string Type { get; init; } = "";
}
