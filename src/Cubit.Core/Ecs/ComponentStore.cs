namespace Cubit.Core.Ecs;

internal sealed class ComponentStore<T> : IComponentStore where T : struct
{
    private T[] _components = new T[4];
    private EntityId[] _entities = new EntityId[4];
    private int[] _sparse = [];

    public Type ComponentType => typeof(T);

    public int Count { get; private set; }

    public void Add(EntityId entity, T component)
    {
        EnsureSparseCapacity(entity.Index);
        if (Contains(entity))
        {
            throw new InvalidOperationException($"实体 {entity} 已包含组件 {typeof(T).FullName}");
        }

        EnsureDenseCapacity();
        _entities[Count] = entity;
        _components[Count] = component;
        _sparse[entity.Index] = Count + 1;
        Count++;
    }

    public bool Contains(EntityId entity)
    {
        if ((uint)entity.Index >= (uint)_sparse.Length)
        {
            return false;
        }

        var denseIndex = _sparse[entity.Index] - 1;
        return (uint)denseIndex < (uint)Count && _entities[denseIndex] == entity;
    }

    public ref T Get(EntityId entity)
    {
        if (!TryGetDenseIndex(entity, out var denseIndex))
        {
            throw new InvalidOperationException($"实体 {entity} 不包含组件 {typeof(T).FullName}");
        }

        return ref _components[denseIndex];
    }

    public bool Remove(EntityId entity)
    {
        if (!TryGetDenseIndex(entity, out var denseIndex))
        {
            return false;
        }

        var lastIndex = Count - 1;
        if (denseIndex != lastIndex)
        {
            var movedEntity = _entities[lastIndex];
            _entities[denseIndex] = movedEntity;
            _components[denseIndex] = _components[lastIndex];
            _sparse[movedEntity.Index] = denseIndex + 1;
        }

        _entities[lastIndex] = default;
        _components[lastIndex] = default;
        _sparse[entity.Index] = 0;
        Count--;
        return true;
    }

    public EntityId EntityAt(int denseIndex)
    {
        if ((uint)denseIndex >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(denseIndex));
        }

        return _entities[denseIndex];
    }

    public ref T ComponentAt(int denseIndex)
    {
        if ((uint)denseIndex >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(denseIndex));
        }

        return ref _components[denseIndex];
    }

    private bool TryGetDenseIndex(EntityId entity, out int denseIndex)
    {
        if ((uint)entity.Index < (uint)_sparse.Length)
        {
            denseIndex = _sparse[entity.Index] - 1;
            if ((uint)denseIndex < (uint)Count && _entities[denseIndex] == entity)
            {
                return true;
            }
        }

        denseIndex = -1;
        return false;
    }

    private void EnsureSparseCapacity(int entityIndex)
    {
        if (entityIndex < _sparse.Length)
        {
            return;
        }

        var newLength = Math.Max(entityIndex + 1, Math.Max(4, _sparse.Length * 2));
        Array.Resize(ref _sparse, newLength);
    }

    private void EnsureDenseCapacity()
    {
        if (Count < _components.Length)
        {
            return;
        }

        var newLength = checked(_components.Length * 2);
        Array.Resize(ref _components, newLength);
        Array.Resize(ref _entities, newLength);
    }
}
