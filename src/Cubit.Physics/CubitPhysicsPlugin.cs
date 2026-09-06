using Cubit.Core.Plugins;
using Cubit.Physics.Scene;
using Cubit.Physics.Shapes;

namespace Cubit.Physics;

/// <summary>Cubit 官方三维物理插件入口。</summary>
public sealed class CubitPhysicsPlugin : IEnginePlugin
{
    private bool _started;

    public string Id => "cubit.physics";

    public Version Version { get; } = new(1, 0, 0);

    /// <summary>当前插件实例是否已进入启动状态。</summary>
    public bool IsStarted => _started;

    public void Register(PluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.RegisterResource<BoxShape3D>();
        registry.RegisterResource<SphereShape3D>();
        registry.RegisterResource<CapsuleShape3D>();
        registry.RegisterResource<PhysicsMaterial3D>();
        registry.RegisterNode<PhysicsWorld3D>();
        registry.RegisterNode<CollisionShape3D>();
        registry.RegisterNode<StaticBody3D>();
        registry.RegisterNode<RigidBody3D>();
        registry.RegisterNode<Area3D>();
        registry.RegisterNode<CharacterBody3D>();
    }

    /// <summary>插件不创建全局世界或后台线程；物理世界由场景节点拥有。</summary>
    public void Start(EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsMainThread)
        {
            throw new InvalidOperationException("物理插件只能在主线程启动");
        }

        if (_started)
        {
            throw new InvalidOperationException("物理插件已经启动");
        }

        _started = true;
    }

    public void Stop()
    {
        _started = false;
    }
}
