namespace Cubit.Core.Plugins;

using Cubit.Core.Project;
using Cubit.Core.Scene;

/// <summary>解析项目声明并管理插件生命周期。</summary>
public sealed class PluginManager : IDisposable
{
    private readonly Dictionary<string, IEnginePlugin> _available;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly List<IEnginePlugin> _resolved = [];
    private readonly List<IEnginePlugin> _started = [];
    private bool _configured;
    private bool _disposed;

    public PluginManager(IEnumerable<IEnginePlugin> availablePlugins, SceneRegistry sceneRegistry)
    {
        ArgumentNullException.ThrowIfNull(availablePlugins);
        ArgumentNullException.ThrowIfNull(sceneRegistry);
        _available = new Dictionary<string, IEnginePlugin>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in availablePlugins)
        {
            ArgumentNullException.ThrowIfNull(plugin);
            var id = NormalizeId(plugin.Id, "可用插件 ID 不能为空");
            if (!_available.TryAdd(id, plugin))
            {
                throw new InvalidOperationException($"可用插件 ID 重复: {id}");
            }

            if (plugin.Version is null)
            {
                throw new InvalidOperationException($"插件 {id} 的版本不能为空");
            }
        }

        SceneRegistry = sceneRegistry;
        Registry = new PluginRegistry(SceneRegistry);
    }

    public IReadOnlyList<IEnginePlugin> ResolvedPlugins => _resolved;

    public PluginRegistry Registry { get; }

    /// <summary>当前运行会话的显式节点注册表。</summary>
    public SceneRegistry SceneRegistry { get; }

    public bool IsStarted { get; private set; }

    public void Configure(IReadOnlyList<CubitPluginReference> declarations)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureOwnerThread();
        ArgumentNullException.ThrowIfNull(declarations);
        if (_configured)
        {
            throw new InvalidOperationException("插件管理器已经配置");
        }

        var declaredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in declarations)
        {
            ArgumentNullException.ThrowIfNull(declaration);
            var id = NormalizeId(declaration.Id, "项目插件 ID 不能为空");
            if (!declaredIds.Add(id))
            {
                throw new InvalidOperationException($"项目插件重复声明: {id}");
            }

            if (!_available.TryGetValue(id, out var plugin))
            {
                throw new InvalidOperationException($"项目声明的插件不可用: {id}");
            }

            if (!Version.TryParse(declaration.Version, out var requestedVersion))
            {
                throw new InvalidOperationException($"插件 {id} 的项目版本无效: {declaration.Version}");
            }

            if (plugin.Version != requestedVersion)
            {
                throw new InvalidOperationException(
                    $"插件 {id} 版本不匹配: 项目要求 {requestedVersion}，可用版本 {plugin.Version}");
            }

            _resolved.Add(plugin);
        }

        try
        {
            foreach (var plugin in _resolved)
            {
                try
                {
                    plugin.Register(Registry.ForPlugin(plugin.Id));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"插件 {plugin.Id} 注册失败: {ex.Message}", ex);
                }
            }

            Registry.Commit();
            _configured = true;
        }
        catch
        {
            Registry.Rollback();
            _resolved.Clear();
            throw;
        }
    }

    public void Start(EngineContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureOwnerThread();
        ArgumentNullException.ThrowIfNull(context);
        if (!_configured)
        {
            throw new InvalidOperationException("必须先配置插件再启动");
        }

        if (IsStarted)
        {
            throw new InvalidOperationException("插件已经启动");
        }

        if (!context.IsMainThread)
        {
            throw new InvalidOperationException("只能在主线程启动插件");
        }

        foreach (var plugin in _resolved)
        {
            try
            {
                plugin.Start(context);
                _started.Add(plugin);
            }
            catch (Exception ex)
            {
                var errors = new List<Exception>
                {
                    new InvalidOperationException($"插件 {plugin.Id} 启动失败: {ex.Message}", ex),
                };
                StopStartedPlugins(errors);
                IsStarted = false;
                throw errors.Count == 1 ? errors[0] : new AggregateException("插件启动失败且回滚不完整", errors);
            }
        }

        IsStarted = true;
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureOwnerThread();
        var errors = new List<Exception>();
        StopStartedPlugins(errors);
        IsStarted = false;
        if (errors.Count > 0)
        {
            throw new AggregateException("一个或多个插件停止失败", errors);
        }
    }

    public void Dispose()
    {
        EnsureOwnerThread();
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }

    private void StopStartedPlugins(List<Exception> errors)
    {
        for (var i = _started.Count - 1; i >= 0; i--)
        {
            var plugin = _started[i];
            try
            {
                plugin.Stop();
            }
            catch (Exception ex)
            {
                errors.Add(new InvalidOperationException($"插件 {plugin.Id} 停止失败: {ex.Message}", ex));
            }
        }

        _started.Clear();
    }

    private static string NormalizeId(string? id, string message) =>
        string.IsNullOrWhiteSpace(id) ? throw new InvalidOperationException(message) : id.Trim();

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("插件管理器只能由创建线程驱动");
        }
    }
}
