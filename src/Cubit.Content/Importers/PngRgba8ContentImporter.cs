using System.Text.Json;
using StbImageSharp;

namespace Cubit.Content.Importers;

/// <summary>将 PNG 解码为紧凑 RGBA8 缓存工件。</summary>
public sealed class PngRgba8ContentImporter : IContentImporter
{
    public string Id => "cubit.png-rgba8";

    public Version Version => new(1, 0, 0);

    public IReadOnlyCollection<string> Extensions => [".png"];

    public ContentArtifact Import(ContentImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var image = ImageResult.FromMemory(context.SourceData.ToArray(), ColorComponents.RedGreenBlueAlpha);
        var expectedLength = checked(image.Width * image.Height * 4);
        if (image.Width <= 0 || image.Height <= 0 || image.Data.Length != expectedLength)
        {
            throw new InvalidDataException("PNG 解码结果不是有效 RGBA8 图像");
        }

        var metadata = JsonSerializer.Serialize(
            new
            {
                width = image.Width,
                height = image.Height,
                channels = 4,
                importer = Id,
                importerVersion = Version.ToString(),
                cacheKey = context.CacheKey,
            });
        return new ContentArtifact(".rgba", image.Data, metadata);
    }
}
