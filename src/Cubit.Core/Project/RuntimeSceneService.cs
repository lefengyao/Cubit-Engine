using Cubit.Core.Scene;

namespace Cubit.Core.Project;

/// <summary>
/// 领域无关的运行时场景服务：按项目场景目录加载、切换和替换 SceneTree 内容。
/// 场景 ID/路径属于项目清单，节点类型属于当前会话 registry；服务不创建作者节点。
/// </summary>
public sealed class RuntimeSceneService
{
    private readonly CubitProject _project;
    private readonly SceneTree _tree;
    private readonly SceneRegistry _registry;
    private readonly Action<Node>? _configureScene;

    public RuntimeSceneService(
        CubitProject project,
        SceneTree tree,
        SceneRegistry registry,
        Action<Node>? configureScene = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _configureScene = configureScene;
    }

    public string? CurrentSceneId { get; private set; }

    public string? CurrentScenePath { get; private set; }

    /// <summary>当前运行树的只读访问入口；场景切换后的项目协调器不得继续使用已脱树 Node.Tree。</summary>
    public SceneTree Tree => _tree;

    /// <summary>按项目场景 ID反序列化 detached scene，不改变当前 SceneTree。</summary>
    public Node Load(string sceneId)
    {
        _tree.EnsureMainThread();
        var entry = _project.SceneCatalog.GetById(sceneId);
        return ProjectIO.LoadScene(_project, entry.Id, _registry);
    }

    /// <summary>
    /// 切换到项目场景。场景文件解析或类型校验失败发生在替换前，旧树保持不变。
    /// </summary>
    public void Switch(string sceneId)
    {
        _tree.EnsureMainThread();
        var entry = _project.SceneCatalog.GetById(sceneId);
        var root = ProjectIO.LoadScene(_project, entry.Id, _registry);
        ReplaceLoadedRoot(root, entry);
    }

    /// <summary>替换当前树中的 detached 根；可选 sceneId 用于更新编辑/运行状态。</summary>
    public void Replace(Node root, string? sceneId = null)
    {
        _tree.EnsureMainThread();
        ArgumentNullException.ThrowIfNull(root);
        ProjectSceneCatalog.Entry? entry = null;
        if (!string.IsNullOrWhiteSpace(sceneId))
        {
            entry = _project.SceneCatalog.GetById(sceneId);
        }

        ReplaceRoot(root, entry);
    }

    private void ReplaceLoadedRoot(Node root, ProjectSceneCatalog.Entry entry)
    {
        ReplaceRoot(root, entry);
    }

    private void ReplaceRoot(Node root, ProjectSceneCatalog.Entry? entry)
    {
        var previousSceneId = CurrentSceneId;
        try
        {
            _configureScene?.Invoke(root);
            if (entry is not null)
            {
                // 场景节点在 ReplaceChildren 中同步进入树并执行 Ready；先提交目标身份，
                // 让新场景的 Ready 能读取与当前节点树一致的 CurrentSceneId。
                SetCurrent(entry);
            }
            else
            {
                CurrentSceneId = null;
                CurrentScenePath = null;
            }

            _tree.ReplaceChildren(root);
        }
        catch (Exception primary)
        {
            if (string.IsNullOrWhiteSpace(previousSceneId))
            {
                throw;
            }

            try
            {
                var previousEntry = _project.SceneCatalog.GetById(previousSceneId);
                var previousRoot = ProjectIO.LoadScene(_project, previousEntry.Id, _registry);
                _configureScene?.Invoke(previousRoot);
                _tree.ReplaceChildren(previousRoot);
                SetCurrent(previousEntry);
            }
            catch (Exception restore)
            {
                throw new AggregateException("场景切换失败且旧场景恢复失败", primary, restore);
            }

            throw;
        }
    }

    private void SetCurrent(ProjectSceneCatalog.Entry entry)
    {
        CurrentSceneId = entry.Id;
        CurrentScenePath = entry.Path;
    }
}
