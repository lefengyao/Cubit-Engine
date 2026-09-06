namespace Cubit.Core.Ecs;

public sealed class EcsSystemContext
{
    internal EcsSystemContext(
        EcsWorld world,
        CommandBuffer commands,
        double delta,
        CancellationToken cancellationToken)
    {
        World = world;
        Commands = commands;
        Delta = delta;
        CancellationToken = cancellationToken;
    }

    public EcsWorld World { get; }

    public CommandBuffer Commands { get; }

    public double Delta { get; }

    public CancellationToken CancellationToken { get; }
}
