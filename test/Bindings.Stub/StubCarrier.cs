using Hosihikari;
using Microsoft.Extensions.Logging;

namespace Hosihikari.Bindings;

/// <summary>
/// 载体绑定的替身：真实的那份由星光产出（ADR-0021），这里只为验证
/// <b>共享层能不能同时吃下契约之外的第二个来源</b>（ADR-0019）。
/// </summary>
public interface ICarrierInfo
{
    string Describe();
}

public sealed class StubCarrier(string name) : ICarrierInfo
{
    public string Describe() => $"stub-carrier:{name}";
}

/// <summary>
/// 检验用：插件把上下文留在这里，模拟外部世界在卸载后仍攥着它。
/// 上下文少带一件插件侧的东西，插件 ALC 就多一个回收不了的根。
/// </summary>
public static class ContextSink
{
    public static object? Held { get; set; }
}

/// <summary>打包期载体组校验用的最小实现（ADR-0061）；运行时不会被装配。</summary>
public sealed class StubCarrierComponent : ICarrierComponent
{
    public ValueTask StartAsync(ICarrierComponentContext context, CancellationToken token) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
}

/// <summary>抽象类：实现了接口但没有无参构造，不能作为组件装配。</summary>
public abstract class AbstractCarrierComponent : ICarrierComponent
{
    public abstract ValueTask StartAsync(ICarrierComponentContext context, CancellationToken token);

    public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
}

/// <summary>运行期组件阶段的检验：记录收到的是总线与日志，按需在启动/停止里挂住。</summary>
public sealed class ProbeCarrierComponent : ICarrierComponent
{
    public static int Started;
    public static int Stopped;
    public static bool HangOnStop;
    public static IEventBus? SeenBus;

    public ValueTask StartAsync(ICarrierComponentContext context, CancellationToken token)
    {
        SeenBus = context.Events;
        context.Logger.LogInformation("probe component started");
        Interlocked.Increment(ref Started);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken token)
    {
        Interlocked.Increment(ref Stopped);
        if (HangOnStop)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
    }
}
