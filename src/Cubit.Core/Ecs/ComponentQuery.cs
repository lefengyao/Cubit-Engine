namespace Cubit.Core.Ecs;

public readonly struct ComponentQuery<T> where T : struct
{
    private readonly ComponentStore<T>? _store;

    internal ComponentQuery(ComponentStore<T>? store)
    {
        _store = store;
    }

    public int Count => _store?.Count ?? 0;

    public Enumerator GetEnumerator() => new(_store);

    public struct Enumerator
    {
        private readonly ComponentStore<T>? _store;
        private int _index;

        internal Enumerator(ComponentStore<T>? store)
        {
            _store = store;
            _index = -1;
        }

        public bool MoveNext() => ++_index < (_store?.Count ?? 0);

        public ComponentQueryItem<T> Current => new(_store!, _index);
    }
}

public readonly struct ComponentQueryItem<T> where T : struct
{
    private readonly ComponentStore<T> _store;
    private readonly int _index;

    internal ComponentQueryItem(ComponentStore<T> store, int index)
    {
        _store = store;
        _index = index;
    }

    public EntityId Entity => _store.EntityAt(_index);

    public ref T Component => ref _store.ComponentAt(_index);
}

public readonly struct ComponentQuery<T1, T2>
    where T1 : struct
    where T2 : struct
{
    private readonly ComponentStore<T1>? _first;
    private readonly ComponentStore<T2>? _second;

    internal ComponentQuery(ComponentStore<T1>? first, ComponentStore<T2>? second)
    {
        _first = first;
        _second = second;
    }

    public int Count
    {
        get
        {
            if (_first is null || _second is null)
            {
                return 0;
            }

            var count = 0;
            for (var i = 0; i < _first.Count; i++)
            {
                if (_second.Contains(_first.EntityAt(i)))
                {
                    count++;
                }
            }

            return count;
        }
    }

    public Enumerator GetEnumerator() => new(_first, _second);

    public struct Enumerator
    {
        private readonly ComponentStore<T1>? _first;
        private readonly ComponentStore<T2>? _second;
        private int _index;

        internal Enumerator(ComponentStore<T1>? first, ComponentStore<T2>? second)
        {
            _first = first;
            _second = second;
            _index = -1;
        }

        public bool MoveNext()
        {
            if (_first is null || _second is null)
            {
                return false;
            }

            while (++_index < _first.Count)
            {
                if (_second.Contains(_first.EntityAt(_index)))
                {
                    return true;
                }
            }

            return false;
        }

        public ComponentQueryItem<T1, T2> Current => new(_first!, _second!, _index);
    }
}

public readonly struct ComponentQueryItem<T1, T2>
    where T1 : struct
    where T2 : struct
{
    private readonly ComponentStore<T1> _first;
    private readonly ComponentStore<T2> _second;
    private readonly int _firstIndex;

    internal ComponentQueryItem(ComponentStore<T1> first, ComponentStore<T2> second, int firstIndex)
    {
        _first = first;
        _second = second;
        _firstIndex = firstIndex;
    }

    public EntityId Entity => _first.EntityAt(_firstIndex);

    public ref T1 Component1 => ref _first.ComponentAt(_firstIndex);

    public ref T2 Component2 => ref _second.Get(Entity);
}
