namespace Hosihikari.Internal;

/// <summary>
/// 插件服务的共享登记处（FX-0027）。键是类型 —— 服务类型来自共享层，所以两边看到同一个类型对象
/// （FX-0026）。它住在核心（默认 ALC）里，因此对登记对象的引用是**强**的：插件忘了摘的东西会把
/// 它的 ALC 钉死，卸载验证会如实失败。收回由 <see cref="PluginScope"/> 负责。
/// </summary>
internal sealed class PluginServiceRegistry
{
    private readonly Dictionary<Type, object> _carrierServices = [];
    private readonly Lock _gate = new();

    internal bool Register<T>(T service) where T : class
    {
        lock (_gate)
        {
            // 先来的留着：两插件抢同一个服务是配置冲突，要让双方都看得见（FX-0009）。
            return _carrierServices.TryAdd(typeof(T), service);
        }
    }

    internal T? Get<T>() where T : class
    {
        lock (_gate)
        {
            return _carrierServices.TryGetValue(typeof(T), out var service) ? (T)service : null;
        }
    }

    internal void Remove(Type type)
    {
        lock (_gate)
        {
            _carrierServices.Remove(type);
        }
    }
}
