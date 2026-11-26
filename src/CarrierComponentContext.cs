using Microsoft.Extensions.Logging;

namespace Hosihikari.Internal;

/// <summary>载体组件的输入面（FX-0061）：总线、日志与载体服务，不给插件面。</summary>
internal sealed class CarrierComponentContext(
    IEventBus events,
    ILogger logger,
    IServiceProvider carrierServices
) : ICarrierComponentContext
{
    public IEventBus Events => events;

    public ILogger Logger => logger;

    public IServiceProvider CarrierServices => carrierServices;
}
