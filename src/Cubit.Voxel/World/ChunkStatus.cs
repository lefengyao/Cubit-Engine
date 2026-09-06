namespace Cubit.Voxel.World;

/// <summary>区块生成状态机（借鉴 MC chunk status）：Empty → Generated → Lighted → Meshed → Ready。</summary>
public enum ChunkStatus
{
    Empty,
    Generated,
    Lighted,
    Meshed,
    Ready,
}

/// <summary>加载等级（借鉴 MC ticket/level）：决定区块参与哪些模拟。</summary>
public enum TicketLevel
{
    DataOnly = 0,
    BlockTicks = 1,
    EntitySimulation = 2,
}

/// <summary>玩家观察点周围的三层区块距离策略。</summary>
public readonly record struct ChunkTicketDistances
{
    public ChunkTicketDistances(int renderRadius, int blockTickRadius, int entitySimulationRadius)
    {
        if (renderRadius < 0 || blockTickRadius < 0 || entitySimulationRadius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(renderRadius), "区块 ticket 半径不能为负数");
        }

        if (entitySimulationRadius > blockTickRadius || blockTickRadius > renderRadius)
        {
            throw new ArgumentException("ticket 半径必须满足 entity <= blockTicks <= render");
        }

        RenderRadius = renderRadius;
        BlockTickRadius = blockTickRadius;
        EntitySimulationRadius = entitySimulationRadius;
    }

    public int RenderRadius { get; }

    public int BlockTickRadius { get; }

    public int EntitySimulationRadius { get; }

    public TicketLevel GetLevel(int centerCx, int centerCz, int cx, int cz)
    {
        var distance = Math.Max(Math.Abs(cx - centerCx), Math.Abs(cz - centerCz));
        if (distance <= EntitySimulationRadius)
        {
            return TicketLevel.EntitySimulation;
        }

        if (distance <= BlockTickRadius)
        {
            return TicketLevel.BlockTicks;
        }

        return TicketLevel.DataOnly;
    }
}
