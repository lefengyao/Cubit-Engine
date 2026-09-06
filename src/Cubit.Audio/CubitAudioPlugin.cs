using Cubit.Core.Diagnostics;
using Cubit.Core.Plugins;

namespace Cubit.Audio;

/// <summary>可选的通用音频插件；不会自行创建平台设备或后台线程。</summary>
public sealed class CubitAudioPlugin : IEnginePlugin
{
    private bool _started;
    private EngineContext? _context;
    private IAudioOutputBackend? _backend;

    public string Id => "cubit.audio";

    public Version Version { get; } = new(1, 0, 0);

    public bool IsStarted => _started;

    public DiagnosticBag Diagnostics { get; } = new();

    public AudioServer? Server { get; private set; }

    public void Register(PluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.RegisterResource<AudioClip>();
        registry.RegisterNode<AudioPlayer>();
        registry.RegisterNode<AudioPlayer3D>();
    }

    public void Start(EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsMainThread)
        {
            throw new InvalidOperationException("音频插件只能在主线程启动");
        }

        if (_started)
        {
            throw new InvalidOperationException("音频插件已经启动");
        }

        if (context.TryGetService<AudioServer>(out _))
        {
            throw new InvalidOperationException("引擎上下文已经注册 AudioServer");
        }

        context.TryGetService<IAudioOutputBackend>(out var backend);
        var server = new AudioServer(backend, Diagnostics);
        try
        {
            context.AddService(server);
        }
        catch
        {
            server.Dispose();
            throw;
        }

        Server = server;
        _context = context;
        _backend = backend;
        _started = true;
    }

    public void Stop()
    {
        var context = _context;
        if (context is not null && !context.IsMainThread)
        {
            throw new InvalidOperationException("音频插件只能在主线程停止");
        }

        var server = Server;
        var backend = _backend;
        if (server is not null)
        {
            context?.RemoveService(server);
        }

        server?.Dispose();
        if (backend is not null)
        {
            context?.RemoveService(backend);
        }

        Server = null;
        _backend = null;
        _context = null;
        _started = false;
    }
}
