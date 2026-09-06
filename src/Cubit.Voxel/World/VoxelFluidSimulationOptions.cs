namespace Cubit.Voxel.World;

/// <summary>动态流体模拟的有界配置；默认关闭以保持旧世界的静态流体行为。</summary>
public sealed record VoxelFluidSimulationOptions
{
    public bool Enabled { get; init; }

    public int FluidTickDelay { get; init; } = 5;

    public int MaxUpdatesPerTick { get; init; } = 4096;

    public int MaxQueuedUpdates { get; init; } = 100_000;

    public void Validate()
    {
        if (FluidTickDelay < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(FluidTickDelay), "流体计划刻延迟必须大于 0");
        }

        if (MaxUpdatesPerTick < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxUpdatesPerTick), "每 tick 流体更新预算必须大于 0");
        }

        if (MaxQueuedUpdates < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedUpdates), "流体计划队列上限必须大于 0");
        }
    }
}
