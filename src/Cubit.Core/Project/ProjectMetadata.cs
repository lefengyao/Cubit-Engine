using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cubit.Core.Project;

/// <summary>
/// 项目私有元数据：为项目源文件维护稳定 UID，并把编辑器生成内容隔离在 .cubit 目录。
/// 参考 Godot 的资源 UID 缓存思路，但不复用其文件格式或目录名。
/// </summary>
public sealed class ProjectMetadata
{
    public const string DirectoryName = ".cubit";
    public const string ImportedDirectoryName = "imported";
    public const string UidCacheFileName = "uid-cache.json";
    public const string UidPrefix = "cuid://";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _rootDirectory;
    private List<ProjectAssetEntry> _files;

    private ProjectMetadata(string rootDirectory, List<ProjectAssetEntry> files)
    {
        _rootDirectory = rootDirectory;
        _files = files;
    }

    public string RootDirectory => _rootDirectory;

    public string MetadataDirectory => ProjectIO.ResolveProjectPath(_rootDirectory, DirectoryName);

    public string ImportedDirectory => ProjectIO.ResolveProjectPath(
        _rootDirectory,
        $"{DirectoryName}/{ImportedDirectoryName}");

    public string UidCachePath => ProjectIO.ResolveProjectPath(
        _rootDirectory,
        $"{DirectoryName}/{UidCacheFileName}");

    public IReadOnlyList<ProjectAssetEntry> Files => _files;

    /// <summary>打开或创建项目 UID 缓存，并同步当前项目源文件列表。</summary>
    public static ProjectMetadata Open(string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            throw new ArgumentException("项目目录不能为空", nameof(projectDirectory));
        }

        var rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        var metadataDirectory = ProjectIO.ResolveProjectPath(rootDirectory, DirectoryName);
        var importedDirectory = ProjectIO.ResolveProjectPath(
            rootDirectory,
            $"{DirectoryName}/{ImportedDirectoryName}");
        Directory.CreateDirectory(metadataDirectory);
        Directory.CreateDirectory(importedDirectory);

        var metadata = new ProjectMetadata(rootDirectory, LoadExisting(rootDirectory));
        metadata.Refresh();
        return metadata;
    }

    /// <summary>按项目相对路径获取资源登记；文件不存在时抛出可诊断异常。</summary>
    public ProjectAssetEntry GetRequired(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        return _files.FirstOrDefault(entry => string.Equals(entry.Path, normalized, StringComparison.Ordinal))
            ?? throw new FileNotFoundException("项目元数据未登记该文件", normalized);
    }

    /// <summary>
    /// 让场景文件内的 UID 成为该路径的权威标识。若同一 UID 已登记到其他路径则拒绝，避免引用歧义。
    /// </summary>
    public ProjectAssetEntry SynchronizeDocumentUid(string relativePath, string? declaredUid)
    {
        var entry = GetRequired(relativePath);
        if (string.IsNullOrWhiteSpace(declaredUid))
        {
            return entry;
        }

        if (!IsValidUid(declaredUid))
        {
            throw new InvalidDataException($"无效的 Cubit 资源 UID: {declaredUid}");
        }

        var conflict = _files.FirstOrDefault(candidate =>
            !ReferenceEquals(candidate, entry) &&
            string.Equals(candidate.Uid, declaredUid, StringComparison.Ordinal));
        if (conflict is not null)
        {
            throw new InvalidDataException(
                $"资源 UID 冲突: {declaredUid} 同时指向 {entry.Path} 与 {conflict.Path}");
        }

        if (!string.Equals(entry.Uid, declaredUid, StringComparison.Ordinal))
        {
            entry.Uid = declaredUid;
            Save();
        }

        return entry;
    }

    /// <summary>通过 UID 解析当前项目内路径；缓存未登记时返回 false。</summary>
    public bool TryResolvePath(string uid, out string relativePath)
    {
        var entry = _files.FirstOrDefault(candidate => string.Equals(candidate.Uid, uid, StringComparison.Ordinal));
        if (entry is null)
        {
            relativePath = string.Empty;
            return false;
        }

        relativePath = entry.Path;
        return true;
    }

    /// <summary>
    /// 扫描项目源文件并更新哈希。路径未变时 UID 保持不变；改名时仅在旧哈希唯一匹配时继承 UID。
    /// </summary>
    public void Refresh()
    {
        var previous = _files.ToArray();
        var byPath = previous.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var matchedPrevious = new HashSet<ProjectAssetEntry>();
        var refreshed = new List<ProjectAssetEntry>();

        foreach (var relativePath in EnumerateSourceFiles())
        {
            var contentHash = ComputeContentHash(relativePath);
            if (byPath.TryGetValue(relativePath, out var existing))
            {
                matchedPrevious.Add(existing);
                refreshed.Add(new ProjectAssetEntry
                {
                    Uid = existing.Uid,
                    Path = relativePath,
                    ContentHash = contentHash,
                });
                continue;
            }

            var renameCandidates = previous
                .Where(entry => !matchedPrevious.Contains(entry) &&
                    string.Equals(entry.ContentHash, contentHash, StringComparison.Ordinal))
                .ToArray();
            var uid = renameCandidates.Length == 1
                ? renameCandidates[0].Uid
                : CreateUid();
            if (renameCandidates.Length == 1)
            {
                matchedPrevious.Add(renameCandidates[0]);
            }

            refreshed.Add(new ProjectAssetEntry
            {
                Uid = uid,
                Path = relativePath,
                ContentHash = contentHash,
            });
        }

        _files = refreshed;
        Save();
    }

    private static List<ProjectAssetEntry> LoadExisting(string rootDirectory)
    {
        var cacheRelativePath = $"{DirectoryName}/{UidCacheFileName}";
        if (!ProjectIO.ProjectFileExists(rootDirectory, cacheRelativePath))
        {
            return [];
        }

        try
        {
            var cache = JsonSerializer.Deserialize<ProjectUidCache>(
                ProjectIO.ReadProjectFileBytes(rootDirectory, cacheRelativePath),
                Options) ?? throw new InvalidDataException("UID 缓存为空");
            if (cache.FormatVersion != ProjectUidCache.CurrentFormatVersion)
            {
                throw new InvalidDataException($"不支持的 UID 缓存版本: {cache.FormatVersion}");
            }

            var files = cache.Files ?? [];
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var uids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in files)
            {
                entry.Path = NormalizeRelativePath(entry.Path);
                if (!IsValidUid(entry.Uid) || string.IsNullOrWhiteSpace(entry.ContentHash) ||
                    !paths.Add(entry.Path) || !uids.Add(entry.Uid))
                {
                    throw new InvalidDataException("UID 缓存包含无效或重复条目");
                }
            }

            return files;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"UID 缓存 JSON 损坏: {exception.Message}", exception);
        }
    }

    private IEnumerable<string> EnumerateSourceFiles()
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(_rootDirectory);
        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(childDirectory);
                if (IsGeneratedDirectory(name))
                {
                    continue;
                }

                if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"项目元数据拒绝扫描链接目录: {childDirectory}");
                }

                pendingDirectories.Push(childDirectory);
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"项目元数据拒绝扫描链接文件: {file}");
                }

                yield return NormalizeRelativePath(Path.GetRelativePath(_rootDirectory, file));
            }
        }
    }

    private static bool IsGeneratedDirectory(string name) =>
        name.Equals(DirectoryName, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
        // 运行时存档不是作者资产；纳入会让相同二进制快照在不同槽位争夺同一 UID。
        name.Equals("saves", StringComparison.OrdinalIgnoreCase);

    private string ComputeContentHash(string relativePath) =>
        Convert.ToHexString(SHA256.HashData(ProjectIO.ReadProjectFileBytes(_rootDirectory, relativePath)))
            .ToLowerInvariant();

    private void Save()
    {
        var cache = new ProjectUidCache
        {
            Files = _files.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToList(),
        };
        ProjectIO.WriteProjectFileBytesAtomically(
            _rootDirectory,
            $"{DirectoryName}/{UidCacheFileName}",
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cache, Options)));
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("UID 缓存路径必须是非空相对路径");
        }

        var normalized = relativePath.Replace('\\', '/');
        if (normalized == "." || normalized == ".." || normalized.StartsWith("../", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"UID 缓存路径越界: {relativePath}");
        }

        return normalized;
    }

    private static string CreateUid() => $"{UidPrefix}{Guid.NewGuid():N}";

    private static bool IsValidUid(string uid) =>
        uid.StartsWith(UidPrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(uid[UidPrefix.Length..], "N", out _);

    private sealed class ProjectUidCache
    {
        public const int CurrentFormatVersion = 1;

        public int FormatVersion { get; init; } = CurrentFormatVersion;

        public List<ProjectAssetEntry>? Files { get; init; } = [];
    }
}

/// <summary>项目源文件的稳定 UID、相对路径及内容哈希。</summary>
public sealed class ProjectAssetEntry
{
    public string Uid { get; set; } = "";

    public string Path { get; set; } = "";

    public string ContentHash { get; set; } = "";
}
