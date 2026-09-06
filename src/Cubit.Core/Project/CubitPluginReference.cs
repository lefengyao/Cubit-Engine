namespace Cubit.Core.Project;

/// <summary>项目清单中的插件标识与精确版本声明。</summary>
public sealed class CubitPluginReference
{
    public string Id { get; init; } = "";

    public string Version { get; init; } = "";
}
