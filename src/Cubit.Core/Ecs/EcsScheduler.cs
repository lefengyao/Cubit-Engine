namespace Cubit.Core.Ecs;

using Cubit.Core.Jobs;

public sealed class EcsScheduler
{
    private readonly EcsWorld _world;
    private readonly JobSystem _jobs;
    private readonly List<IEcsSystem> _systems = [];
    private readonly HashSet<string> _systemIds = new(StringComparer.OrdinalIgnoreCase);

    public EcsScheduler(EcsWorld world, JobSystem jobs)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
    }

    public void Register(IEcsSystem system)
    {
        _world.EnsureMainThread();
        ArgumentNullException.ThrowIfNull(system);
        var descriptor = system.Descriptor
            ?? throw new InvalidOperationException("ECS System 描述不能为空");
        if (string.IsNullOrWhiteSpace(descriptor.Id))
        {
            throw new InvalidOperationException("ECS System ID 不能为空");
        }

        if (descriptor.Reads is null || descriptor.Writes is null)
        {
            throw new InvalidOperationException($"System {descriptor.Id} 的读写集合不能为空");
        }

        if (descriptor.Reads.Any(type => type is null) || descriptor.Writes.Any(type => type is null))
        {
            throw new InvalidOperationException($"System {descriptor.Id} 的读写集合不能包含空类型");
        }

        if (!_systemIds.Add(descriptor.Id.Trim()))
        {
            throw new InvalidOperationException($"ECS System ID 重复: {descriptor.Id}");
        }

        _systems.Add(system);
    }

    public IReadOnlyList<IReadOnlyList<IEcsSystem>> BuildBatches(EcsSystemPhase phase)
    {
        var sorted = _systems
            .Where(system => system.Descriptor.Phase == phase)
            .OrderBy(system => system.Descriptor.Order)
            .ThenBy(system => system.Descriptor.Id, StringComparer.Ordinal)
            .ToArray();
        var batches = new List<List<IEcsSystem>>();
        var assignedBatch = new Dictionary<IEcsSystem, int>(ReferenceEqualityComparer.Instance);

        foreach (var system in sorted)
        {
            var minimumBatch = 0;
            foreach (var prior in assignedBatch)
            {
                if (IsBarrier(system, prior.Key) || Conflicts(system.Descriptor, prior.Key.Descriptor))
                {
                    minimumBatch = Math.Max(minimumBatch, prior.Value + 1);
                }
            }

            var targetBatch = minimumBatch;
            while (targetBatch < batches.Count && !CanShareBatch(system, batches[targetBatch]))
            {
                targetBatch++;
            }

            if (targetBatch == batches.Count)
            {
                batches.Add([]);
            }

            batches[targetBatch].Add(system);
            assignedBatch.Add(system, targetBatch);
        }

        return batches
            .Select(batch => (IReadOnlyList<IEcsSystem>)batch.ToArray())
            .ToArray();
    }

    public void ExecutePhase(
        EcsSystemPhase phase,
        double delta,
        CancellationToken cancellationToken = default)
    {
        _world.EnsureMainThread();
        if (!double.IsFinite(delta) || delta < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "ECS phase delta 必须是非负有限数");
        }

        foreach (var batch in BuildBatches(phase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = batch
                .Select(system => (System: system, Commands: new CommandBuffer()))
                .ToArray();

            try
            {
                if (batch.Count == 1 && batch[0].Descriptor.RequiresMainThread)
                {
                    ExecuteSystem(entries[0].System, entries[0].Commands, delta, cancellationToken);
                }
                else
                {
                    var actions = entries.Select(entry => (Action<CancellationToken>)(token =>
                        ExecuteSystem(entry.System, entry.Commands, delta, token)));
                    _jobs.ScheduleBatch(actions, $"ecs-{phase}", cancellationToken).Complete();
                }
            }
            catch (Exception ex)
            {
                var ids = string.Join(',', batch.Select(system => system.Descriptor.Id));
                throw new InvalidOperationException($"ECS phase {phase} 批次失败 [{ids}]: {ex.Message}", ex);
            }

            foreach (var entry in entries)
            {
                try
                {
                    entry.Commands.Playback(_world);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"System {entry.System.Descriptor.Id} 的结构命令提交失败: {ex.Message}", ex);
                }
            }
        }
    }

    private void ExecuteSystem(
        IEcsSystem system,
        CommandBuffer commands,
        double delta,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var access = _world.BeginSystemAccess(system.Descriptor);
        system.Execute(new EcsSystemContext(_world, commands, delta, cancellationToken));
    }

    private static bool CanShareBatch(IEcsSystem system, IReadOnlyList<IEcsSystem> batch) =>
        !system.Descriptor.RequiresMainThread &&
        batch.All(other => !other.Descriptor.RequiresMainThread &&
            !Conflicts(system.Descriptor, other.Descriptor));

    private static bool IsBarrier(IEcsSystem left, IEcsSystem right) =>
        left.Descriptor.RequiresMainThread || right.Descriptor.RequiresMainThread;

    private static bool Conflicts(EcsSystemDescriptor left, EcsSystemDescriptor right) =>
        left.Writes.Overlaps(right.Writes) ||
        left.Writes.Overlaps(right.Reads) ||
        right.Writes.Overlaps(left.Reads);
}
