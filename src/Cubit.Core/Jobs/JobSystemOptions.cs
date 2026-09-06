namespace Cubit.Core.Jobs;

public sealed class JobSystemOptions
{
    public int WorkerCount { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);
}
