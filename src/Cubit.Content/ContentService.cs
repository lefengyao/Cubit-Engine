using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cubit.Core.Diagnostics;
using Cubit.Core.Project;

namespace Cubit.Content;

/// <summary>项目内容导入服务的插件边界。</summary>
public sealed class ContentService
{
    private const string CacheRootRelativePath = "Library/cubit-content";
    private readonly Dictionary<string, IContentImporter> _importersByExtension =
        new(StringComparer.OrdinalIgnoreCase);

    public ContentService(IEnumerable<IContentImporter> importers)
    {
        ArgumentNullException.ThrowIfNull(importers);
        var importerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var importer in importers)
        {
            ArgumentNullException.ThrowIfNull(importer);
            if (string.IsNullOrWhiteSpace(importer.Id) || !importerIds.Add(importer.Id.Trim()))
            {
                throw new ArgumentException("内容导入器 ID 不能为空且不能重复", nameof(importers));
            }

            if (importer.Version is null)
            {
                throw new ArgumentException($"内容导入器 {importer.Id} 缺少版本", nameof(importers));
            }

            foreach (var extension in importer.Extensions)
            {
                var normalizedExtension = NormalizeExtension(extension);
                if (!_importersByExtension.TryAdd(normalizedExtension, importer))
                {
                    throw new ArgumentException($"内容导入器扩展名重复: {normalizedExtension}", nameof(importers));
                }
            }
        }
    }

    public ContentImportResult Import(ContentImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string sourcePath;
        try
        {
            sourcePath = ProjectIO.ResolveProjectPath(request.ProjectDirectory, request.SourceRelativePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Fail("content.path.invalid", "内容源路径无效", request.SourceRelativePath);
        }

        byte[] sourceData;
        try
        {
            sourceData = ProjectIO.ReadProjectFileBytes(request.ProjectDirectory, request.SourceRelativePath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Fail("content.source.missing", "找不到要导入的内容源文件", request.SourceRelativePath);
        }
        catch (InvalidDataException)
        {
            return Fail("content.path.invalid", "内容源路径无效", request.SourceRelativePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Fail("content.import.failed", "无法读取要导入的内容源文件", request.SourceRelativePath);
        }

        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrEmpty(extension) || !_importersByExtension.TryGetValue(extension, out var importer))
        {
            return Fail("content.importer.missing", "找不到与内容源文件匹配的导入器", request.SourceRelativePath);
        }

        string settingsJson;
        try
        {
            settingsJson = ContentJson.NormalizeObject(request.SettingsJson);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return Fail("content.import.failed", "内容导入设置不是有效 JSON 对象", request.SourceRelativePath);
        }

        var cacheKey = CreateCacheKey(
            sourceData,
            importer,
            NormalizeExtension(extension).ToLowerInvariant(),
            settingsJson);
        try
        {
            using (ProjectCacheGate.Enter(request.ProjectDirectory))
            {
                var index = ReadIndex(request.ProjectDirectory);
                if (index.TryGetValue(cacheKey, out var existing) &&
                    TryResolveExistingArtifact(request.ProjectDirectory, cacheKey, importer, existing, out var existingArtifactPath))
                {
                    return new ContentImportResult(true, true, cacheKey, existingArtifactPath, []);
                }

                ContentArtifact artifact;
                try
                {
                    artifact = importer.Import(new ContentImportContext(sourcePath, sourceData, settingsJson, cacheKey));
                    ValidateArtifact(artifact, importer, cacheKey);
                }
                catch (Exception)
                {
                    return Fail("content.import.failed", "内容导入器无法处理源文件", request.SourceRelativePath);
                }

                var artifactRelativePath = $"{CacheRootRelativePath}/{cacheKey}/artifact{artifact.ArtifactExtension}";
                var metadataRelativePath = $"{CacheRootRelativePath}/{cacheKey}/metadata.json";
                ProjectIO.WriteProjectFileBytesAtomically(request.ProjectDirectory, artifactRelativePath, artifact.Data);
                ProjectIO.WriteProjectFileBytesAtomically(
                    request.ProjectDirectory,
                    metadataRelativePath,
                    Encoding.UTF8.GetBytes(artifact.MetadataJson));

                index[cacheKey] = new ContentIndexEntry(
                    artifactRelativePath,
                    metadataRelativePath,
                    importer.Id,
                    importer.Version.ToString());
                WriteIndex(request.ProjectDirectory, index);
                var artifactPath = ProjectIO.ResolveProjectPath(request.ProjectDirectory, artifactRelativePath);
                return new ContentImportResult(true, false, cacheKey, artifactPath, []);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return Fail("content.cache.write", "无法发布内容导入缓存", request.SourceRelativePath);
        }
    }

    private static ContentImportResult Fail(string code, string message, string? sourcePath) =>
        new(
            Succeeded: false,
            CacheHit: false,
            CacheKey: null,
            ArtifactPath: null,
            Diagnostics:
            [
                new EngineDiagnostic(code, DiagnosticSeverity.Error, message, sourcePath),
            ]);

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            throw new ArgumentException("内容导入器扩展名不能为空", nameof(extension));
        }

        var normalized = extension.Trim();
        if (!normalized.StartsWith('.'))
        {
            normalized = $".{normalized}";
        }

        if (normalized.Any(character => char.IsWhiteSpace(character) || Path.GetInvalidFileNameChars().Contains(character)))
        {
            throw new ArgumentException($"内容导入器扩展名无效: {extension}", nameof(extension));
        }

        return normalized;
    }

    private static string CreateCacheKey(
        byte[] sourceData,
        IContentImporter importer,
        string normalizedExtension,
        string settingsJson)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendBytes(hash, sourceData);
        AppendText(hash, importer.Id);
        AppendText(hash, importer.Version.ToString());
        AppendText(hash, normalizedExtension);
        AppendText(hash, settingsJson);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendText(IncrementalHash hash, string value) =>
        AppendBytes(hash, Encoding.UTF8.GetBytes(value));

    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static SortedDictionary<string, ContentIndexEntry> ReadIndex(string projectDirectory)
    {
        var indexRelativePath = $"{CacheRootRelativePath}/content-index.json";
        var indexPath = ProjectIO.ResolveProjectPath(projectDirectory, indexRelativePath);
        if (!File.Exists(indexPath))
        {
            return new SortedDictionary<string, ContentIndexEntry>(StringComparer.Ordinal);
        }

        var indexBytes = ProjectIO.ReadProjectFileBytes(projectDirectory, indexRelativePath);
        using var document = JsonDocument.Parse(indexBytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("内容缓存索引格式无效");
        }

        var result = new SortedDictionary<string, ContentIndexEntry>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("内容缓存索引条目无效");
            }

            var indexEntry = new ContentIndexEntry(
                ReadRequiredString(entry.Value, "artifactPath"),
                ReadRequiredString(entry.Value, "metadataPath"),
                ReadRequiredString(entry.Value, "importerId"),
                ReadRequiredString(entry.Value, "importerVersion"));
            if (!IsCacheKey(entry.Name) || !HasExpectedCacheLayout(entry.Name, indexEntry))
            {
                continue;
            }

            if (!result.TryAdd(entry.Name, indexEntry))
            {
                throw new InvalidDataException("内容缓存索引包含重复条目");
            }
        }

        return result;
    }

    private static bool TryResolveExistingArtifact(
        string projectDirectory,
        string cacheKey,
        IContentImporter importer,
        ContentIndexEntry entry,
        out string artifactPath)
    {
        artifactPath = string.Empty;
        if (!HasExpectedCacheLayout(cacheKey, entry) ||
            !string.Equals(entry.ImporterId, importer.Id, StringComparison.Ordinal) ||
            !string.Equals(entry.ImporterVersion, importer.Version.ToString(), StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var metadataData = ProjectIO.ReadProjectFileBytes(projectDirectory, entry.MetadataPath);
            if (!ProjectIO.ProjectFileExists(projectDirectory, entry.ArtifactPath))
            {
                return false;
            }

            if (!MetadataMatches(metadataData, importer, cacheKey))
            {
                return false;
            }

            artifactPath = ProjectIO.ResolveProjectPath(projectDirectory, entry.ArtifactPath);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return false;
        }
    }

    private static void WriteIndex(string projectDirectory, SortedDictionary<string, ContentIndexEntry> index)
    {
        var entries = index.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new { formatVersion = 1, entries },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
        ProjectIO.WriteProjectFileBytesAtomically(
            projectDirectory,
            $"{CacheRootRelativePath}/content-index.json",
            bytes);
    }

    private static string ReadRequiredString(JsonElement entry, string propertyName)
    {
        if (!entry.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"内容缓存索引缺少有效字段: {propertyName}");
        }

        return property.GetString()!.Trim();
    }

    private static bool IsCacheKey(string value) =>
        value.Length == 64 && value.All(character =>
            (character >= '0' && character <= '9') ||
            (character >= 'a' && character <= 'f'));

    private static bool HasExpectedCacheLayout(string cacheKey, ContentIndexEntry entry)
    {
        if (!IsCacheKey(cacheKey))
        {
            return false;
        }

        var cacheDirectory = $"{CacheRootRelativePath}/{cacheKey}";
        if (!string.Equals(entry.MetadataPath, $"{cacheDirectory}/metadata.json", StringComparison.Ordinal) ||
            !entry.ArtifactPath.StartsWith($"{cacheDirectory}/artifact", StringComparison.Ordinal))
        {
            return false;
        }

        var extension = entry.ArtifactPath[(cacheDirectory.Length + "/artifact".Length)..];
        try
        {
            _ = NormalizeExtension(extension);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void ValidateArtifact(ContentArtifact artifact, IContentImporter importer, string cacheKey)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        _ = NormalizeExtension(artifact.ArtifactExtension);
        ArgumentNullException.ThrowIfNull(artifact.Data);
        var metadataData = Encoding.UTF8.GetBytes(artifact.MetadataJson);
        if (!MetadataMatches(metadataData, importer, cacheKey))
        {
            throw new InvalidDataException("内容导入器元数据不匹配当前缓存项");
        }
    }

    private static bool MetadataMatches(byte[] metadataData, IContentImporter importer, string cacheKey)
    {
        using var metadata = JsonDocument.Parse(metadataData);
        if (metadata.RootElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return HasExactStringProperty(metadata.RootElement, "importer", importer.Id) &&
            HasExactStringProperty(metadata.RootElement, "importerVersion", importer.Version.ToString()) &&
            HasExactStringProperty(metadata.RootElement, "cacheKey", cacheKey);
    }

    private static bool HasExactStringProperty(JsonElement element, string propertyName, string expectedValue) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        string.Equals(property.GetString(), expectedValue, StringComparison.Ordinal);

    private sealed class ProjectCacheGate : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _entered;

        private ProjectCacheGate(Mutex mutex)
        {
            _mutex = mutex;
            _entered = true;
        }

        public static ProjectCacheGate Enter(string projectDirectory)
        {
            var rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
            var cacheRootIdentity = OperatingSystem.IsWindows()
                ? rootDirectory.ToUpperInvariant()
                : rootDirectory;
            var nameHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheRootIdentity)));
            var mutex = new Mutex(false, $"cubit-content-{nameHash}");
            try
            {
                try
                {
                    _ = mutex.WaitOne();
                }
                catch (AbandonedMutexException)
                {
                    // 上一个进程异常退出后，当前线程已取得互斥锁并负责重建可再生缓存。
                }

                return new ProjectCacheGate(mutex);
            }
            catch
            {
                mutex.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (!_entered)
            {
                return;
            }

            _entered = false;
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    private sealed record ContentIndexEntry(
        string ArtifactPath,
        string MetadataPath,
        string ImporterId,
        string ImporterVersion);
}
