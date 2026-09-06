namespace Cubit.Core.Scene;

/// <summary>
/// 项目内打包场景的显式实例占位节点。
/// 运行时由 PackedScene 展开其子树；展开生成的子节点不会作为源场景内容保存。
/// </summary>
public sealed class SceneInstance : Node
{
    [Export("场景路径")]
    public string ScenePath { get; set; } = "";

    [Export("场景 UID")]
    public string SceneUid { get; set; } = "";

    /// <summary>是否把目标根的子节点直接插入当前父节点；默认保留实例包装节点。</summary>
    [Export("展平实例")]
    public bool Flatten { get; set; }
}
