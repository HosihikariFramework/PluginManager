using Hosihikari;

namespace Hosihikari.Testing;

/// <summary>内存服务注册表：同类型先到先得，与框架契约一致。</summary>
public sealed class TestServiceRegistry : IPluginServiceRegistry
{
    private readonly Dictionary<Type, object> _services = [];

    public bool Register<T>(T service)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        return _services.TryAdd(typeof(T), service);
    }

    public T? Get<T>()
        where T : class => _services.TryGetValue(typeof(T), out var service) ? (T)service : null;

    internal bool Remove(Type type) => _services.Remove(type);
}
