using Cubit.Core.Plugins;
using Cubit.Voxel.Input;
using Cubit.Voxel.Physics;
using Cubit.Voxel.World;

namespace Cubit.Voxel;

/// <summary>Cubit 官方体素插件入口。</summary>
public sealed class CubitVoxelPlugin : IEnginePlugin
{
    public string Id => "cubit.voxel";

    public Version Version { get; } = new(1, 0, 0);

    public void Register(PluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.RegisterNode<VoxelWorldNode>();
        registry.RegisterNode<VoxelTerrainBody3D>();
        registry.RegisterResource<WorldConfig>();
    }

    public void Start(EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.AddService(new VoxelInputState());
    }

    public void Stop()
    {
    }
}
