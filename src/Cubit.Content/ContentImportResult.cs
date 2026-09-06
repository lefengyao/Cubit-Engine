using Cubit.Core.Diagnostics;

namespace Cubit.Content;

/// <summary>内容导入的可诊断结果。</summary>
public sealed record ContentImportResult(
    bool Succeeded,
    bool CacheHit,
    string? CacheKey,
    string? ArtifactPath,
    IReadOnlyList<EngineDiagnostic> Diagnostics);
