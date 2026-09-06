using System.Text;
using Cubit.Core.Scene;

namespace Cubit.Core.Project;

/// <summary>
/// 项目内作者场景的显式打包/实例化上下文。
/// 只允许引用当前项目场景目录中的文件，并以根 UID 校验引用身份。
/// </summary>
public sealed class PackedScene
{
    private readonly SceneRegistry _registry;

    private PackedScene(
        CubitProject project,
        ProjectSceneCatalog.Entry entry,
        SceneRegistry registry)
    {
        Project = project;
        Entry = entry;
        _registry = registry;
    }

    public CubitProject Project { get; }

    public ProjectSceneCatalog.Entry Entry { get; }

    /// <summary>打开项目场景描述；这里只解析目录和 UID，不修改 SceneTree。</summary>
    public static PackedScene Open(CubitProject project, string sceneId, SceneRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);
        ArgumentNullException.ThrowIfNull(registry);
        var entry = project.SceneCatalog.GetById(sceneId);
        ValidateDocument(project, entry);
        return new PackedScene(project, entry, registry);
    }

    /// <summary>将场景及其显式 SceneInstance 引用展开为脱离 SceneTree 的节点树。</summary>
    public Node Instantiate()
    {
        var activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Entry.Path,
        };
        return InstantiateEntry(Entry, activePaths);
    }

    private Node InstantiateEntry(
        ProjectSceneCatalog.Entry entry,
        HashSet<string> activePaths)
    {
        ValidateDocument(Project, entry);
        var json = Encoding.UTF8.GetString(
            ProjectIO.ReadProjectFileBytes(Project.RootDirectory, entry.Path));
        var root = SceneIO.DeserializeFromJson(json, _registry);
        ExpandReferences(root, activePaths);
        return root;
    }

    private void ExpandReferences(Node node, HashSet<string> activePaths)
    {
        foreach (var child in node.Children.ToArray())
        {
            if (child is SceneInstance instance)
            {
                var target = ResolveReference(instance);
                if (!activePaths.Add(target.Path))
                {
                    throw new InvalidDataException(
                        $"场景实例引用形成循环: {string.Join(" -> ", activePaths)} -> {target.Path}");
                }

                try
                {
                    var packed = InstantiateEntry(target, activePaths);
                    if (instance.Flatten)
                    {
                        node.RemoveChild(instance);
                        foreach (var targetChild in packed.Children.ToArray())
                        {
                            packed.RemoveChild(targetChild);
                            node.AddChild(targetChild);
                        }
                    }
                    else
                    {
                        foreach (var targetChild in packed.Children.ToArray())
                        {
                            packed.RemoveChild(targetChild);
                            instance.AddChild(targetChild);
                        }
                    }
                }
                finally
                {
                    activePaths.Remove(target.Path);
                }

                continue;
            }

            ExpandReferences(child, activePaths);
        }
    }

    private ProjectSceneCatalog.Entry ResolveReference(SceneInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.ScenePath) ||
            string.IsNullOrWhiteSpace(instance.SceneUid))
        {
            throw new InvalidDataException(
                $"场景实例 {instance.Name} 必须同时声明 ScenePath 和 SceneUid");
        }

        ProjectSceneCatalog.Entry target;
        try
        {
            target = Project.SceneCatalog.GetByPath(instance.ScenePath);
        }
        catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException)
        {
            throw new InvalidDataException(
                $"场景实例 {instance.Name} 引用了无效项目内路径: {instance.ScenePath}", exception);
        }

        if (!string.Equals(target.Uid, instance.SceneUid, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"场景实例 {instance.Name} 的 UID 与目标文件不匹配: {instance.SceneUid} != {target.Uid}");
        }

        return target;
    }

    private static void ValidateDocument(
        CubitProject project,
        ProjectSceneCatalog.Entry entry)
    {
        var json = Encoding.UTF8.GetString(
            ProjectIO.ReadProjectFileBytes(project.RootDirectory, entry.Path));
        var declaredUid = SceneIO.GetDocumentUid(json);
        if (!string.Equals(declaredUid, entry.Uid, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"场景文件根 UID 与项目目录不匹配: {entry.Path} ({declaredUid ?? "missing"} != {entry.Uid})");
        }
    }
}
