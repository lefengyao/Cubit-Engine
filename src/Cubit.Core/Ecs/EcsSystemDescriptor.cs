using System.Collections.Frozen;

namespace Cubit.Core.Ecs;

public sealed record EcsSystemDescriptor
{
    public EcsSystemDescriptor(
        string Id,
        EcsSystemPhase Phase,
        IReadOnlySet<Type> Reads,
        IReadOnlySet<Type> Writes,
        int Order = 0,
        bool RequiresMainThread = false,
        bool StructuralChanges = false)
    {
        ArgumentNullException.ThrowIfNull(Reads);
        ArgumentNullException.ThrowIfNull(Writes);
        this.Id = Id;
        this.Phase = Phase;
        this.Reads = Reads.ToFrozenSet();
        this.Writes = Writes.ToFrozenSet();
        this.Order = Order;
        this.RequiresMainThread = RequiresMainThread;
        this.StructuralChanges = StructuralChanges;
    }

    public string Id { get; }

    public EcsSystemPhase Phase { get; }

    public IReadOnlySet<Type> Reads { get; }

    public IReadOnlySet<Type> Writes { get; }

    public int Order { get; }

    public bool RequiresMainThread { get; }

    public bool StructuralChanges { get; }
}
