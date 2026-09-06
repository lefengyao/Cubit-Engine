using Cubit.Core.Plugins;

namespace Cubit.Animation;

/// <summary>Cubit 官方通用时间轴动画插件入口。</summary>
public sealed class CubitAnimationPlugin : IEnginePlugin
{
    private bool _started;

    public string Id => "cubit.animation";

    public Version Version { get; } = new(1, 0, 0);

    public bool IsStarted => _started;

    public void Register(PluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.RegisterResource<AnimationClip>();
        registry.RegisterResource<AnimationTrack>();
        registry.RegisterNode<Skeleton3D>();
        registry.RegisterNode<Bone3D>();
        registry.RegisterNode<AnimationPlayer>();
        registry.RegisterNode<Tween>();
    }

    public void Start(EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsMainThread)
        {
            throw new InvalidOperationException("动画插件只能在主线程启动");
        }

        if (_started)
        {
            throw new InvalidOperationException("动画插件已经启动");
        }

        _started = true;
    }

    public void Stop()
    {
        _started = false;
    }
}
