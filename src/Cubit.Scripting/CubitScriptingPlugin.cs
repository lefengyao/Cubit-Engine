using Cubit.Core.Diagnostics;
using Cubit.Core.Plugins;
using Cubit.Core.Scene;

namespace Cubit.Scripting;

/// <summary>受控 C# 脚本插件；不扫描项目目录、不自行创建线程，也不承诺热重载。</summary>
public sealed class CubitScriptingPlugin : IEnginePlugin
{
    private bool _started;
    private int _ownerThreadId;
    private ScriptRuntime? _runtime;

    public string Id => "cubit.scripting";

    public Version Version { get; } = new(1, 0, 0);

    public DiagnosticBag Diagnostics { get; } = new();

    public bool IsStarted => _started;

    public void Register(PluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.RegisterResource<ScriptAttachment>();
        registry.RegisterNode<ScriptAttachmentNode>();
    }

    public void Start(EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsMainThread)
        {
            throw new InvalidOperationException("脚本插件只能在主线程启动");
        }

        if (_started)
        {
            throw new InvalidOperationException("脚本插件已经启动");
        }

        _ownerThreadId = Environment.CurrentManagedThreadId;
        _started = true;
    }

    public ScriptRuntime CreateRuntime(SceneTree tree, string projectRoot, DiagnosticBag? diagnostics = null)
    {
        EnsureOwnerThread();
        if (!_started)
        {
            throw new InvalidOperationException("脚本插件尚未启动");
        }

        if (_runtime is not null)
        {
            throw new InvalidOperationException("脚本插件已经拥有活动运行时");
        }

        _runtime = new ScriptRuntime(tree, projectRoot, diagnostics ?? Diagnostics, ReleaseRuntime);
        return _runtime;
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        EnsureOwnerThread();
        _runtime?.Dispose();
        _runtime = null;
        _started = false;
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("脚本插件只能由启动线程访问");
        }
    }

    private void ReleaseRuntime(ScriptRuntime runtime)
    {
        if (ReferenceEquals(_runtime, runtime))
        {
            _runtime = null;
        }
    }
}
