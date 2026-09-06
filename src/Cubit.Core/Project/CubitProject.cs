namespace Cubit.Core.Project;

/// <summary>已验证并规范化的 Cubit 项目。</summary>
public sealed record CubitProject(
    string RootDirectory,
    string ManifestPath,
    string MainScenePath,
    CubitProjectManifest Manifest,
    ProjectMetadata Metadata,
    ProjectSceneCatalog SceneCatalog);
