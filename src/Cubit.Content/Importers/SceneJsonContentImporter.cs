using System.Text.Json;

namespace Cubit.Content.Importers;

/// <summary>将 Cubit 场景 JSON 规范化为缓存工件。</summary>
public sealed class SceneJsonContentImporter : IContentImporter
{
    public string Id => "cubit.scene-json";

    public Version Version => new(1, 0, 0);

    public IReadOnlyCollection<string> Extensions => [".cscene"];

    public ContentArtifact Import(ContentImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var data = ContentJson.NormalizeObject(context.SourceData, true);
        var metadata = JsonSerializer.Serialize(new
        {
            importer = Id,
            importerVersion = Version.ToString(),
            cacheKey = context.CacheKey,
        });
        return new ContentArtifact(".json", data, metadata);
    }
}
