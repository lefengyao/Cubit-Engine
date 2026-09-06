namespace Cubit.Content;

/// <summary>可由 ContentService 显式调用的确定性文件导入器。</summary>
public interface IContentImporter
{
    string Id { get; }

    Version Version { get; }

    IReadOnlyCollection<string> Extensions { get; }

    ContentArtifact Import(ContentImportContext context);
}
