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

    /// <summary>Loader 在钩子之外发现插件死了时走这里（ADR-0030）。诊断的可读说明进管理器的账。</summary>
    public void ReportFault(PluginDiagnostic diagnostic) =>
        host.ReportFault(name, instanceId, diagnostic.Message);

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
