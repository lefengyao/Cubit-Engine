namespace Cubit.Core.Ecs;

public sealed class EcsWorld
{
    [ThreadStatic]
    private static SystemAccessScope? _currentAccess;

    private sealed class SystemAccessScope : IDisposable
    {
        private readonly SystemAccessScope? _previous;

        public SystemAccessScope(EcsWorld world, EcsSystemDescriptor descriptor)
        {
            World = world;
            Descriptor = descriptor;
            _previous = _currentAccess;
            _currentAccess = this;
        }

        public EcsWorld World { get; }

        public EcsSystemDescriptor Descriptor { get; }

        public void Dispose() => _currentAccess = _previous;
    }

    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;
    private readonly List<int> _generations = [];
    private readonly List<bool> _alive = [];
    private readonly Stack<int> _freeIndices = [];
    private readonly Dictionary<Type, IComponentStore> _componentStores = [];

    public int EntityCount { get; private set; }

    public EntityId CreateEntity()
    {
        EnsureDirectStructuralAccess();
        int index;
        if (_freeIndices.TryPop(out var reusedIndex))
        {
            index = reusedIndex;
            _alive[index] = true;
        }
        else
        {
            index = _generations.Count;
            _generations.Add(1);
            _alive.Add(true);
        }

        EntityCount++;
        return new EntityId(index, _generations[index]);
    }

    public void DestroyEntity(EntityId entity)
    {
        EnsureDirectStructuralAccess();
        EnsureAlive(entity);
        foreach (var store in _componentStores.Values)
        {
            store.Remove(entity);
        }

        _alive[entity.Index] = false;
        _generations[entity.Index] = _generations[entity.Index] == int.MaxValue
            ? 1
            : _generations[entity.Index] + 1;
        _freeIndices.Push(entity.Index);
        EntityCount--;
    }

    public bool IsAlive(EntityId entity) =>
        entity.IsValid &&
        (uint)entity.Index < (uint)_generations.Count &&
        _alive[entity.Index] &&
        _generations[entity.Index] == entity.Generation;

    public void AddComponent<T>(EntityId entity, T component) where T : struct
    {
        EnsureDirectStructuralAccess();
        EnsureAlive(entity);
        GetOrCreateStore<T>().Add(entity, component);
    }

    public bool RemoveComponent<T>(EntityId entity) where T : struct
    {
        EnsureDirectStructuralAccess();
        EnsureAlive(entity);
        return TryGetStore<T>()?.Remove(entity) ?? false;
    }

    public bool HasComponent<T>(EntityId entity) where T : struct
    {
        EnsureComponentAccess<T>();
        return IsAlive(entity) && (TryGetStore<T>()?.Contains(entity) ?? false);
    }

    public ref T GetComponent<T>(EntityId entity) where T : struct
    {
        EnsureComponentAccess<T>();
        EnsureAlive(entity);
        var store = TryGetStore<T>()
            ?? throw new InvalidOperationException($"实体 {entity} 不包含组件 {typeof(T).FullName}");
        return ref store.Get(entity);
    }

    public ComponentQuery<T> Query<T>() where T : struct
    {
        EnsureComponentAccess<T>();
        return new ComponentQuery<T>(TryGetStore<T>());
    }

    public ComponentQuery<T1, T2> Query<T1, T2>()
        where T1 : struct
        where T2 : struct
    {
        EnsureComponentAccess<T1>();
        EnsureComponentAccess<T2>();
        return new ComponentQuery<T1, T2>(TryGetStore<T1>(), TryGetStore<T2>());
    }

    internal IDisposable BeginSystemAccess(EcsSystemDescriptor descriptor) => new SystemAccessScope(this, descriptor);

    internal void EnsureMainThread()
    {
        if (Environment.CurrentManagedThreadId != _mainThreadId)
        {
            throw new InvalidOperationException("ECS 结构变更只能在世界主线程提交");
        }
    }

    private void EnsureDirectStructuralAccess()
    {
        if (_currentAccess is { World: var world, Descriptor: var descriptor } && ReferenceEquals(world, this))
        {
            throw new InvalidOperationException(
                $"System {descriptor.Id} 不能直接改变 ECS 结构；请使用 CommandBuffer");
        }

        EnsureMainThread();
    }

    private void EnsureComponentAccess<T>() where T : struct
    {
        if (_currentAccess is not { World: var world, Descriptor: var descriptor } || !ReferenceEquals(world, this))
        {
            return;
        }

        var type = typeof(T);
        if (!descriptor.Reads.Contains(type) && !descriptor.Writes.Contains(type))
        {
            throw new InvalidOperationException($"System {descriptor.Id} 未声明组件访问: {type.FullName}");
        }
    }

    private ComponentStore<T> GetOrCreateStore<T>() where T : struct
    {
        if (_componentStores.TryGetValue(typeof(T), out var existing))
        {
            return (ComponentStore<T>)existing;
        }

        var store = new ComponentStore<T>();
        _componentStores.Add(typeof(T), store);
        return store;
    }

    private ComponentStore<T>? TryGetStore<T>() where T : struct =>
        _componentStores.TryGetValue(typeof(T), out var store) ? (ComponentStore<T>)store : null;

    private void EnsureAlive(EntityId entity)
    {
        if (!IsAlive(entity))
        {
            throw new InvalidOperationException($"ECS 实体句柄已失效: {entity}");
        }
    }
}
