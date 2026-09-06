using System.Text;
using System.Text.Json;
using Cubit.Core.Scene;

namespace Cubit.Core.Project;

/// <summary>Cubit 项目创建与加载入口；统一执行版本和项目内路径验证。</summary>
public static class ProjectIO
{
    public const int CurrentFormatVersion = 1;
    public const string ManifestFileName = "project.cubit.json";
    public const string DefaultMainScene = "scenes/main.cscene";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static CubitProject Create(string projectDirectory, string? projectName = null)
    {
        var rootDirectory = NormalizeRoot(projectDirectory);
        var name = string.IsNullOrWhiteSpace(projectName)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(rootDirectory))
            : projectName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("项目名称不能为空", nameof(projectName));
        }

        var rootExisted = Directory.Exists(rootDirectory);
        if (rootExisted && Directory.EnumerateFileSystemEntries(rootDirectory).Any())
        {
            throw new InvalidOperationException($"目标目录不是空目录，拒绝覆盖: {rootDirectory}");
        }

        var manifest = new CubitProjectManifest
        {
            FormatVersion = CurrentFormatVersion,
            Name = name,
            MainScene = DefaultMainScene,
            Scenes = [new CubitProjectSceneReference { Id = "main", Path = DefaultMainScene }],
        };
        var manifestPath = ResolveProjectPath(rootDirectory, ManifestFileName);
        var mainScenePath = ResolveProjectPath(rootDirectory, manifest.MainScene);
        var sceneDirectory = Path.GetDirectoryName(mainScenePath)!;
        var sceneDirectoryExisted = Directory.Exists(sceneDirectory);
        var manifestCreated = false;
        var sceneCreated = false;

        try
        {
            Directory.CreateDirectory(sceneDirectory);
            var sceneRoot = new Node { Name = "Root" };
            WriteNewFile(mainScenePath, SceneIO.SerializeToJson(sceneRoot), ref sceneCreated, rootDirectory);
            WriteNewFile(manifestPath, JsonSerializer.Serialize(manifest, Options), ref manifestCreated, rootDirectory);
            return Load(rootDirectory);
        }
        catch
        {
            if (manifestCreated && File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
            }

            if (sceneCreated && File.Exists(mainScenePath))
            {
                File.Delete(mainScenePath);
            }

            if (!sceneDirectoryExisted && Directory.Exists(sceneDirectory) &&
                !Directory.EnumerateFileSystemEntries(sceneDirectory).Any())
            {
                Directory.Delete(sceneDirectory);
            }

            if (!rootExisted && Directory.Exists(rootDirectory) &&
                !Directory.EnumerateFileSystemEntries(rootDirectory).Any())
            {
                Directory.Delete(rootDirectory);
            }

            throw;
        }
    }

    public static CubitProject Load(string projectDirectory)
    {
        var rootDirectory = NormalizeRoot(projectDirectory);
        var manifestPath = ResolveProjectPath(rootDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("找不到 Cubit 项目清单", manifestPath);
        }

        CubitProjectManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CubitProjectManifest>(ReadValidatedFile(manifestPath, rootDirectory), Options)
                ?? throw new InvalidDataException("项目清单为空");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"项目清单 JSON 损坏: {ex.Message}", ex);
        }

        if (manifest.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException($"不支持的项目格式版本: {manifest.FormatVersion}");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new InvalidDataException("项目名称不能为空");
        }

        ValidatePreview(rootDirectory, manifest.Preview);
        ValidateRuntime(manifest.Runtime);
        ValidateEditor(rootDirectory, manifest.Editor);
        ValidateDesktopPresentation(manifest.DesktopPresentation);
        _ = ProjectModuleCatalog.ResolveLoadOrder(manifest.Modules);

        var mainScenePath = ResolveProjectPath(rootDirectory, manifest.MainScene);
        if (!File.Exists(mainScenePath))
        {
            throw new FileNotFoundException("找不到项目主场景", mainScenePath);
        }

        var metadata = ProjectMetadata.Open(rootDirectory);
        SynchronizeSceneDocumentUids(rootDirectory, metadata);
        var sceneCatalog = new ProjectSceneCatalog(
            rootDirectory,
            manifest.MainScene,
            manifest.Scenes,
            metadata);

        return new CubitProject(rootDirectory, manifestPath, mainScenePath, manifest, metadata, sceneCatalog);
    }

    private static void ValidatePreview(string rootDirectory, CubitProjectPreviewReference? preview)
    {
        if (preview is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(preview.Assembly) || string.IsNullOrWhiteSpace(preview.Type))
        {
            throw new InvalidDataException("项目预览声明必须同时提供 assembly 与 type");
        }

        // 仅验证路径位于项目内；程序集可能在用户首次构建前尚不存在。
        _ = ResolveProjectPath(rootDirectory, preview.Assembly);
    }

    private static void ValidateRuntime(CubitProjectRuntimeReference? runtime)
    {
        if (runtime is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(runtime.Assembly) || string.IsNullOrWhiteSpace(runtime.Type))
        {
            throw new InvalidDataException("项目 runtime 声明必须同时提供 assembly 与 type");
        }

        if (runtime.Assembly.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            Path.IsPathRooted(runtime.Assembly))
        {
            throw new InvalidDataException("项目 runtime assembly 必须是已静态链接的程序集简单名称");
        }

        try
        {
            var assemblyName = new System.Reflection.AssemblyName(runtime.Assembly);
            if (!string.Equals(assemblyName.Name, runtime.Assembly, StringComparison.Ordinal))
            {
                throw new InvalidDataException("项目 runtime assembly 必须是程序集简单名称");
            }
        }
        catch (FileLoadException exception)
        {
            throw new InvalidDataException("项目 runtime assembly 无效", exception);
        }
    }

    private static void ValidateEditor(string rootDirectory, CubitProjectEditorReference? editor)
    {
        if (editor is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(editor.Assembly) || string.IsNullOrWhiteSpace(editor.Type))
        {
            throw new InvalidDataException("项目 editor 声明必须同时提供 assembly 与 type");
        }

        if (Path.IsPathRooted(editor.Assembly))
        {
            throw new InvalidDataException("项目 editor assembly 必须是项目内相对路径");
        }

        _ = ResolveProjectPath(rootDirectory, editor.Assembly);
    }

    private static void ValidateDesktopPresentation(CubitProjectDesktopPresentationReference? presentation)
    {
        if (presentation is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(presentation.Assembly) || string.IsNullOrWhiteSpace(presentation.Type))
        {
            throw new InvalidDataException("项目 desktopPresentation 声明必须同时提供 assembly 与 type");
        }

        if (presentation.Assembly.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            Path.IsPathRooted(presentation.Assembly))
        {
            throw new InvalidDataException("项目 desktopPresentation assembly 必须是已静态链接的程序集简单名称");
        }

        try
        {
            var assemblyName = new System.Reflection.AssemblyName(presentation.Assembly);
            if (!string.Equals(assemblyName.Name, presentation.Assembly, StringComparison.Ordinal))
            {
                throw new InvalidDataException("项目 desktopPresentation assembly 必须是程序集简单名称");
            }
        }
        catch (FileLoadException exception)
        {
            throw new InvalidDataException("项目 desktopPresentation assembly 无效", exception);
        }
    }

    /// <summary>项目内每个 .cscene 都在文件头保存其缓存 UID，避免主场景以外的引用在改名后失去身份。</summary>
    private static void SynchronizeSceneDocumentUids(string rootDirectory, ProjectMetadata metadata)
    {
        var changed = false;
        foreach (var scene in metadata.Files.Where(entry =>
                     entry.Path.EndsWith(".cscene", StringComparison.OrdinalIgnoreCase)))
        {
            var scenePath = ResolveProjectPath(rootDirectory, scene.Path);
            var sceneJson = ReadValidatedFile(scenePath, rootDirectory);
            var declaredUid = SceneIO.GetDocumentUid(sceneJson);
            var entry = metadata.SynchronizeDocumentUid(scene.Path, declaredUid);
            if (string.IsNullOrWhiteSpace(declaredUid) || SceneIO.HasNestedUidProperty(sceneJson))
            {
                WriteValidatedFile(
                    scenePath,
                    SceneIO.WithDocumentUid(sceneJson, entry.Uid),
                    FileMode.Open,
                    rootDirectory);
                changed = true;
            }
        }

        if (changed)
        {
            metadata.Refresh();
        }
    }

    /// <summary>使用项目运行会话的显式节点注册表加载主场景。</summary>
    public static Node LoadMainScene(CubitProject project, SceneRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(registry);
        var mainScenePath = ResolveProjectPath(project.RootDirectory, project.Manifest.MainScene);
        return PackedScene.Open(project, project.SceneCatalog.MainScene.Id, registry).Instantiate();
    }

    /// <summary>按项目场景 ID 加载作者场景，并严格使用调用方会话注册表。</summary>
    public static Node LoadScene(CubitProject project, string sceneId, SceneRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(registry);
        var scene = project.SceneCatalog.GetById(sceneId);
        return PackedScene.Open(project, scene.Id, registry).Instantiate();
    }

    /// <summary>按项目场景 ID解析作者场景的绝对路径。</summary>
    public static string GetScenePath(CubitProject project, string sceneId)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.SceneCatalog.GetById(sceneId).AbsolutePath;
    }

    public static void SaveMainScene(CubitProject project, Node root)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(root);
        SaveScene(project, project.SceneCatalog.MainScene.Id, root);
    }

    /// <summary>按项目场景 ID 保存当前作者场景，保留该场景根 UID。</summary>
    public static void SaveScene(CubitProject project, string sceneId, Node root)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);
        ArgumentNullException.ThrowIfNull(root);
        var scene = project.SceneCatalog.GetById(sceneId);
        var sceneEntry = project.Metadata.GetRequired(scene.Path);
        var json = SceneIO.SerializeToJson(root, sceneEntry.Uid);
        WriteValidatedFile(scene.AbsolutePath, json, FileMode.OpenOrCreate, project.RootDirectory);
        project.Metadata.Refresh();
    }

    /// <summary>读取项目内文件的不可变字节快照，并复用项目路径校验。</summary>
    public static byte[] ReadProjectFileBytes(string projectDirectory, string relativePath)
    {
        var rootDirectory = NormalizeRoot(projectDirectory);
        var path = ResolveProjectPath(rootDirectory, relativePath);
        using var stream = OpenValidatedFile(path, FileMode.Open, FileAccess.Read, rootDirectory);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>确认项目内普通文件可在路径复验前后被安全打开，不读取其内容。</summary>
    public static bool ProjectFileExists(string projectDirectory, string relativePath)
    {
        var rootDirectory = NormalizeRoot(projectDirectory);
        var path = ResolveProjectPath(rootDirectory, relativePath);
        try
        {
            using var stream = OpenValidatedFile(path, FileMode.Open, FileAccess.Read, rootDirectory);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>以临时文件发布项目内二进制文件，写入前后均复验项目路径边界。</summary>
    public static void WriteProjectFileBytesAtomically(string projectDirectory, string relativePath, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var rootDirectory = NormalizeRoot(projectDirectory);
        var targetPath = ResolveProjectPath(rootDirectory, relativePath);
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidDataException("项目文件目标路径缺少目录");
        Directory.CreateDirectory(targetDirectory);
        EnsureNoReparsePoints(targetDirectory, rootDirectory);
        targetPath = ResolveProjectPath(rootDirectory, relativePath);

        var temporaryPath = Path.Combine(
            targetDirectory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = OpenValidatedFile(temporaryPath, FileMode.CreateNew, FileAccess.Write, rootDirectory))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            EnsureNoReparsePoints(temporaryPath, rootDirectory);
            EnsureNoReparsePoints(targetPath, rootDirectory);
            File.Move(temporaryPath, targetPath, true);
            EnsureNoReparsePoints(targetPath, rootDirectory);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                EnsureNoReparsePoints(temporaryPath, rootDirectory);
                File.Delete(temporaryPath);
            }
        }
    }

    public static string ResolveProjectPath(string projectDirectory, string relativePath)
    {
        var rootDirectory = NormalizeRoot(projectDirectory);
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("项目资源路径必须是非空相对路径");
        }

        var candidate = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
        var relative = Path.GetRelativePath(rootDirectory, candidate);
        if (relative == "." || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new InvalidDataException($"项目资源路径越界: {relativePath}");
        }

        EnsureNoReparsePoints(candidate, rootDirectory);
        return candidate;
    }

    private static string NormalizeRoot(string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            throw new ArgumentException("项目目录不能为空", nameof(projectDirectory));
        }

        var rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        // 项目根由调用方显式选择，允许它是工作区 Junction；仅拒绝根目录内部的重解析点。
        EnsureNoReparsePoints(rootDirectory, rootDirectory);
        return rootDirectory;
    }

    private static void WriteNewFile(string path, string content, ref bool created, string projectRoot)
    {
        using var stream = OpenValidatedFile(path, FileMode.CreateNew, FileAccess.Write, projectRoot);
        created = true;
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string ReadValidatedFile(string path, string projectRoot)
    {
        using var stream = OpenValidatedFile(path, FileMode.Open, FileAccess.Read, projectRoot);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void WriteValidatedFile(string path, string content, FileMode mode, string projectRoot)
    {
        using var stream = OpenValidatedFile(path, mode, FileAccess.Write, projectRoot);
        stream.SetLength(0);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static FileStream OpenValidatedFile(string path, FileMode mode, FileAccess access, string projectRoot)
    {
        EnsureNoReparsePoints(path, projectRoot);
        var stream = new FileStream(path, mode, access, FileShare.None);
        try
        {
            // 打开后再复验，缩短路径组件被替换后跟随到项目外的竞态窗口。
            EnsureNoReparsePoints(path, projectRoot);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void EnsureNoReparsePoints(string path, string projectRoot)
    {
        var fullPath = Path.GetFullPath(path);
        var trustedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (!IsSameOrAncestor(trustedRoot, fullPath))
        {
            throw new InvalidDataException($"请求路径不在项目根目录内: {path}");
        }

        var pathRoot = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException($"无法解析项目路径根目录: {path}");
        var relative = Path.GetRelativePath(pathRoot, fullPath);
        var current = pathRoot;

        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (IsSameOrAncestor(current, trustedRoot))
            {
                continue;
            }

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"项目路径包含链接或重解析点，拒绝访问: {current}");
            }
        }
    }

    private static bool IsSameOrAncestor(string candidateAncestor, string path)
    {
        if (string.Equals(candidateAncestor, path, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var separator = candidateAncestor.EndsWith(Path.DirectorySeparatorChar) ||
            candidateAncestor.EndsWith(Path.AltDirectorySeparatorChar)
                ? candidateAncestor
                : candidateAncestor + Path.DirectorySeparatorChar;
        return path.StartsWith(separator, StringComparison.OrdinalIgnoreCase);
    }
}
