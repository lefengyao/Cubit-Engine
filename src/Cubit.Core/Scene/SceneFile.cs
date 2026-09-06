using System.Text.Json;

namespace Cubit.Core.Scene;

/// <summary>场景文件（类 .tscn）：节点类型 + 名称 + [Export] 属性 + 子节点。</summary>
public sealed class SceneFile
{
    /// <summary>项目元数据分配的稳定场景 UID；旧场景缺失时由 ProjectIO 一次性补齐。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("uid")]
    public string? Uid { get; set; }

    public string Type { get; set; } = "";

    public string Name { get; set; } = "Node";

    public Dictionary<string, JsonElement>? Properties { get; set; }

    public List<SceneFile>? Children { get; set; }
}
