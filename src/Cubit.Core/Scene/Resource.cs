using System.Text.Json;

namespace Cubit.Core.Scene;

/// <summary>
/// 资源基类（借鉴 Godot Resource）：数据即资源，可 JSON 序列化复用。
/// 方块定义、世界配置、将来的场景文件都从这里派生。
/// </summary>
public class Resource
{
    [Export("资源名")]
    public string ResourceName { get; set; } = "";

    public string? ResourcePath { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, GetType(), JsonOptions);

    public static T FromJson<T>(string json) where T : Resource
        => JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidOperationException("资源反序列化失败");
}
