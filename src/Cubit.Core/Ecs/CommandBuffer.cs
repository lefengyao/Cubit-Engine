namespace Cubit.Core.Ecs;

public sealed class CommandBuffer
{
    private interface ICommand
    {
        EntityTarget? Target { get; }

        void Apply(EcsWorld world, IReadOnlyDictionary<int, EntityId> deferredEntities);
    }

    private sealed record CreateEntityCommand(int DeferredIndex) : ICommand
    {
        public EntityTarget? Target => null;

        public void Apply(EcsWorld world, IReadOnlyDictionary<int, EntityId> deferredEntities)
        {
            ((Dictionary<int, EntityId>)deferredEntities).Add(DeferredIndex, world.CreateEntity());
        }
    }

    private sealed record DestroyEntityCommand(EntityTarget EntityTarget) : ICommand
    {
        public EntityTarget? Target => EntityTarget;

        public void Apply(EcsWorld world, IReadOnlyDictionary<int, EntityId> deferredEntities) =>
            world.DestroyEntity(Resolve(EntityTarget, deferredEntities));
    }

    private sealed record AddComponentCommand<T>(EntityTarget EntityTarget, T Component) : ICommand
        where T : struct
    {
        public EntityTarget? Target => EntityTarget;

        public void Apply(EcsWorld world, IReadOnlyDictionary<int, EntityId> deferredEntities) =>
            world.AddComponent(Resolve(EntityTarget, deferredEntities), Component);
    }

    private sealed record RemoveComponentCommand<T>(EntityTarget EntityTarget) : ICommand where T : struct
    {
        public EntityTarget? Target => EntityTarget;

        public void Apply(EcsWorld world, IReadOnlyDictionary<int, EntityId> deferredEntities) =>
            world.RemoveComponent<T>(Resolve(EntityTarget, deferredEntities));
    }

    private readonly List<ICommand> _commands = [];
    private int _nextDeferredIndex;
    private bool _playing;

    public int Count => _commands.Count;

    public EntityTarget CreateEntity()
    {
        EnsureRecording();
        var deferredIndex = _nextDeferredIndex++;
        _commands.Add(new CreateEntityCommand(deferredIndex));
        return new EntityTarget(default, deferredIndex);
    }

    public void DestroyEntity(EntityTarget target)
    {
        EnsureRecording();
        _commands.Add(new DestroyEntityCommand(target));
    }

    public void AddComponent<T>(EntityTarget target, T component) where T : struct
    {
        EnsureRecording();
        _commands.Add(new AddComponentCommand<T>(target, component));
    }

    public void RemoveComponent<T>(EntityTarget target) where T : struct
    {
        EnsureRecording();
        _commands.Add(new RemoveComponentCommand<T>(target));
    }

    public void Playback(EcsWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.EnsureMainThread();
        if (_playing)
        {
            throw new InvalidOperationException("CommandBuffer 不允许重入 Playback");
        }

        ValidateTargets(world);
        _playing = true;
        try
        {
            var deferredEntities = new Dictionary<int, EntityId>();
            foreach (var command in _commands)
            {
                command.Apply(world, deferredEntities);
            }

            _commands.Clear();
            _nextDeferredIndex = 0;
        }
        finally
        {
            _playing = false;
        }
    }

    private void ValidateTargets(EcsWorld world)
    {
        var declaredDeferred = new HashSet<int>();
        foreach (var command in _commands)
        {
            if (command is CreateEntityCommand create)
            {
                declaredDeferred.Add(create.DeferredIndex);
                continue;
            }

            if (command.Target is not { } target)
            {
                continue;
            }

            if (target.IsDeferred)
            {
                if (!declaredDeferred.Contains(target.DeferredIndex))
                {
                    throw new InvalidOperationException($"CommandBuffer 引用了尚未创建的延迟实体: {target.DeferredIndex}");
                }
            }
            else if (!world.IsAlive(target.Entity))
            {
                throw new InvalidOperationException($"CommandBuffer 引用了失效实体: {target.Entity}");
            }
        }
    }

    private static EntityId Resolve(
        EntityTarget target,
        IReadOnlyDictionary<int, EntityId> deferredEntities)
    {
        if (!target.IsDeferred)
        {
            return target.Entity;
        }

        return deferredEntities.TryGetValue(target.DeferredIndex, out var entity)
            ? entity
            : throw new InvalidOperationException($"找不到延迟实体: {target.DeferredIndex}");
    }

    private void EnsureRecording()
    {
        if (_playing)
        {
            throw new InvalidOperationException("CommandBuffer Playback 期间不能记录命令");
        }
    }
}
