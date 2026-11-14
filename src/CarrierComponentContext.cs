using Microsoft.Extensions.Logging;

namespace Hosihikari.Internal;

/// <summary>载体组件的输入面（ADR-0061）：只有总线与日志，不给插件面。</summary>
internal sealed class CarrierComponentContext(IEventBus events, ILogger logger) : ICarrierComponentContext
{
    public IEventBus Events => events;

    public ILogger Logger => logger;
}
