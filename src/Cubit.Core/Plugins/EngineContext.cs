namespace Cubit.Core.Plugins;

/// <summary>插件启动时接收的引擎服务上下文。</summary>
public sealed class EngineContext
{
    private readonly Dictionary<Type, object> _services = [];
    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;

    public bool IsMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    public EngineContext AddService<T>(T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!IsMainThread)
        {
            throw new InvalidOperationException("只能在主线程注册引擎服务");
        }

        if (!_services.TryAdd(typeof(T), service))
        {
            throw new InvalidOperationException($"引擎服务已注册: {typeof(T).FullName}");
        }

        return this;
    }

    /// <summary>仅移除当前上下文持有的同一服务实例。</summary>
    public bool RemoveService<T>(T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!IsMainThread)
        {
            throw new InvalidOperationException("只能在主线程移除引擎服务");
        }

        return _services.TryGetValue(typeof(T), out var registered) &&
            ReferenceEquals(registered, service) &&
            _services.Remove(typeof(T));
    }

    public T GetRequiredService<T>() where T : class
    {
        if (_services.TryGetValue(typeof(T), out var service))
        {
            return (T)service;
        }

        throw new InvalidOperationException($"找不到引擎服务: {typeof(T).FullName}");
    }

    public bool TryGetService<T>(out T? service) where T : class
    {
        if (_services.TryGetValue(typeof(T), out var value))
        {
            service = (T)value;
            return true;
        }

        service = null;
        return false;
    }

    /// <summary>按运行时类型读取已注册服务，供不依赖项目领域程序集的扩展组合使用。</summary>
    public bool TryGetService(Type type, out object? service)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _services.TryGetValue(type, out service);
    }
}
