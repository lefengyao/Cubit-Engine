namespace Cubit.Content;

/// <summary>导入器生成的缓存载荷与元数据。</summary>
public sealed record ContentArtifact(
    string ArtifactExtension,
    byte[] Data,
    string MetadataJson);
