using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hosihikari.Internal;

internal sealed class PluginContext(
    string name,
    string packageDirectory,
    string dataDirectory,
    ILogger logger,
    IConfiguration configuration,
    PluginScope scope,
    IServiceProvider carrierServices,
    PluginManager host,
    Guid instanceId,
    bool isLoader,
    Action<string> reportFault) : ILoaderContext
{
    internal static readonly IServiceProvider Empty = new EmptyServices();

    public string Name => name;

    public string PackageDirectory => packageDirectory;

    public string DataDirectory => dataDirectory;

    public ILogger Logger => logger;

    public IConfiguration Configuration => configuration;

    public IEventBus Events => scope;

    public IScheduler Scheduler => scope;

    public IPluginServiceRegistry PluginServices => scope;

    public IServiceProvider CarrierServices => carrierServices;

    /// <summary>Loader 在钩子之外发现插件死了时走这里（ADR-0030）。诊断整条进管理器的账。</summary>
    public void ReportFault(PluginDiagnostic diagnostic) =>
        host.ReportFault(name, instanceId, diagnostic);

    /// <summary>
    /// 交给插件的那一份：加载器包拿到 <see cref="ILoaderContext"/>（可转），普通包拿到一个只实现
    /// <see cref="IPluginContext"/> 的视图（ADR-0059）—— 转不回 <see cref="ILoaderContext"/> 就是运行期的分流。
    /// </summary>
    public IPluginContext ForPlugin() => isLoader ? this : new PlainContext(this);

    private sealed class PlainContext(PluginContext inner) : IPluginContext
    {
        public string Name => inner.Name;
        public string PackageDirectory => inner.PackageDirectory;
        public string DataDirectory => inner.DataDirectory;
        public ILogger Logger => inner.Logger;
        public IConfiguration Configuration => inner.Configuration;
        public IEventBus Events => inner.Events;
        public IScheduler Scheduler => inner.Scheduler;
        public IPluginServiceRegistry PluginServices => inner.PluginServices;
        public IServiceProvider CarrierServices => inner.CarrierServices;
        public void ReportFault(PluginDiagnostic diagnostic) => inner.ReportFault(diagnostic);
    }

    /// <summary>Loader 是个插件包，装载时用这个把自己交出去（ADR-0002）。</summary>
    public bool RegisterLoader(IPluginLoader loader)
    {
        if (!host.TryAddLoader(loader, out var problem))
        {
            if (problem is not null)
            {
                reportFault(problem);
            }

            return false;
        }

        host.MarkLoader(instanceId, loader);
        return true;
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
