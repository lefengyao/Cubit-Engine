namespace Cubit.Core.Project;

/// <summary>
/// 项目作者场景目录。它只保存清单声明的场景索引，不持有运行时 SceneTree。
/// </summary>
public sealed class ProjectSceneCatalog
{
    private static readonly HashSet<string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        "flow", "ui", "gameplay", "entities", "audio", "other",
    };

    public sealed record Entry(string Id, string Path, string Category, string AbsolutePath, string Uid);

    private readonly string _projectRoot;
    private readonly Dictionary<string, Entry> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _byPath = new(StringComparer.Ordinal);

    public ProjectSceneCatalog(
        string projectRoot,
        string mainScene,
        IReadOnlyList<CubitProjectSceneReference>? declarations,
        ProjectMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(mainScene);
        ArgumentNullException.ThrowIfNull(metadata);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        _projectRoot = root;
        var references = declarations is { Count: > 0 }
            ? declarations
            : [new CubitProjectSceneReference { Id = "main", Path = mainScene }];

        foreach (var declaration in references)
        {
            ArgumentNullException.ThrowIfNull(declaration);
            var id = declaration.Id?.Trim() ?? "";
            if (id.Length == 0)
            {
                throw new InvalidDataException("项目场景 ID 不能为空");
            }

            var path = NormalizePath(root, declaration.Path);
            if (!path.EndsWith(".cscene", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"项目场景必须是 .cscene 文件: {path}");
            }

            var absolutePath = ProjectIO.ResolveProjectPath(root, path);
            if (!File.Exists(absolutePath))
            {
                throw new FileNotFoundException($"找不到项目场景: {path}", absolutePath);
            }

            var metadataEntry = metadata.GetRequired(path);
            if (!IsValidUid(metadataEntry.Uid))
            {
                throw new InvalidDataException($"项目场景 UID 无效: {path}");
            }

            var category = NormalizeCategory(declaration.Category, id);
            var entry = new Entry(id, path, category, absolutePath, metadataEntry.Uid);
            if (!_byId.TryAdd(id, entry))
            {
                throw new InvalidDataException($"项目场景 ID 重复: {id}");
            }

            if (!_byPath.TryAdd(path, entry))
            {
                throw new InvalidDataException($"项目场景路径重复: {path}");
            }

            if (_byId.Values.Count(entryItem => string.Equals(entryItem.Uid, entry.Uid, StringComparison.Ordinal)) > 1)
            {
                throw new InvalidDataException($"项目场景 UID 重复: {entry.Uid}");
            }
        }

        var normalizedMainScene = NormalizePath(root, mainScene);
        if (!_byPath.ContainsKey(normalizedMainScene))
        {
            throw new InvalidDataException($"mainScene 未在项目场景目录中声明: {normalizedMainScene}");
        }

        MainScene = _byPath[normalizedMainScene];
        Entries = _byId.Values.OrderBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<Entry> Entries { get; }

    public Entry MainScene { get; }

    public Entry GetById(string id) =>
        !string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id.Trim(), out var entry)
            ? entry
            : throw new KeyNotFoundException($"项目场景 ID 不存在: {id}");

    public Entry GetByPath(string path)
    {
        var normalized = NormalizePath(_projectRoot, path);
        return _byPath.TryGetValue(normalized, out var entry)
            ? entry
            : throw new KeyNotFoundException($"项目场景路径不存在: {path}");
    }

    public bool TryGetById(string id, out Entry? entry)
    {
        if (!string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id.Trim(), out var found))
        {
            entry = found;
            return true;
        }

        entry = null;
        return false;
    }

    private static string NormalizePath(string projectRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("项目场景路径必须是非空相对路径");
        }

        var absolute = ProjectIO.ResolveProjectPath(projectRoot, relativePath);
        var normalized = Path.GetRelativePath(projectRoot, absolute).Replace('\\', '/');
        if (normalized is "." or ".." || normalized.StartsWith("../", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"项目场景路径越界: {relativePath}");
        }

        return normalized;
    }

    private static bool IsValidUid(string uid) =>
        uid.StartsWith(ProjectMetadata.UidPrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(uid[ProjectMetadata.UidPrefix.Length..], "N", out _);

    private static string NormalizeCategory(string? category, string sceneId)
    {
        var normalized = string.IsNullOrWhiteSpace(category) ? InferCategory(sceneId) : category.Trim().ToLowerInvariant();
        if (!Categories.Contains(normalized))
        {
            throw new InvalidDataException($"项目场景分类无效: {sceneId} -> {category}");
        }

        return normalized;
    }

    private static string InferCategory(string sceneId) => sceneId.ToLowerInvariant() switch
    {
        "boot" or "title" or "world-selection" or "create-world" => "flow",
        "gameplay" => "gameplay",
        _ => "other",
    };
}
