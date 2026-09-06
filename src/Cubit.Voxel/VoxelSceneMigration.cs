using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cubit.Voxel;

/// <summary>官方体素插件的一次性旧场景类型名升级器。</summary>
public static class VoxelSceneMigration
{
    private const string LegacyWorldType = "Cubit.Core.World.VoxelWorldNode";
    private const string CurrentWorldType = "Cubit.Voxel.World.VoxelWorldNode";

    public static bool MigrateLegacyScene(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var original = File.ReadAllText(path);
        var root = JsonNode.Parse(original) ?? throw new InvalidDataException("场景 JSON 为空");
        if (!ReplaceStrings(root))
        {
            return false;
        }

        var temporary = path + ".migrating";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
        return true;
    }

    private static bool ReplaceStrings(JsonNode node)
    {
        var changed = false;
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (property.Value is JsonValue value && value.TryGetValue<string>(out var text) && text == LegacyWorldType)
                {
                    obj[property.Key] = CurrentWorldType;
                    changed = true;
                }
                else if (property.Value is not null)
                {
                    changed |= ReplaceStrings(property.Value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(child => child is not null))
            {
                changed |= ReplaceStrings(child!);
            }
        }

        return changed;
    }
}
