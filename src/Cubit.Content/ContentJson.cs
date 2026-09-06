using System.Text;
using System.Text.Json;

namespace Cubit.Content;

/// <summary>内容导入使用的确定性 JSON 规范化工具。</summary>
internal static class ContentJson
{
    public static string NormalizeObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("导入设置必须是 JSON 对象");
        }

        return Encoding.UTF8.GetString(NormalizeObject(Encoding.UTF8.GetBytes(json), false));
    }

    public static byte[] NormalizeObject(ReadOnlyMemory<byte> json, bool indented)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("JSON 根节点必须是对象");
        }

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = indented }))
        {
            WriteElement(writer, document.RootElement);
        }

        return output.ToArray();
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
