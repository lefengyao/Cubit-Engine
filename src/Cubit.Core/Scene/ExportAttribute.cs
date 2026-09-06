namespace Cubit.Core.Scene;

/// <summary>
/// 导出属性标记（借鉴 Godot @export）：标在属性/字段上，
/// 供将来的资源编辑器识别为可编辑参数。
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class ExportAttribute : Attribute
{
    public string? Name { get; init; }

    public ExportAttribute() { }

    public ExportAttribute(string name) => Name = name;
}
