namespace Cubit.Core.Project;

/// <summary>编辑器集成入口的显式程序集与类型声明；可选实现项目编辑器扩展注册接口。</summary>
public sealed class CubitProjectEditorReference
{
    /// <summary>项目内程序集相对路径；编辑器不会扫描项目目录寻找程序集。</summary>
    public string Assembly { get; init; } = "";

    /// <summary>实现 <see cref="IProjectRuntimeBootstrap"/> 的公开具体类型。</summary>
    public string Type { get; init; } = "";
}
