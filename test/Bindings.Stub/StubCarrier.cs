using Hosihikari;
using Microsoft.Extensions.DependencyInjection;
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

/// <summary>配置器注册的探针服务：断言从组件上下文的载体服务里取得到，且带着原生句柄。</summary>
public sealed class ProbeCarrierService(nint hostHandle)
{
    public nint HostHandle { get; } = hostHandle;
}

/// <summary>载体包的服务配置器（ADR-0064）：记下句柄与包目录，并往容器里注册一个探针服务。</summary>
public sealed class ProbeConfigurator : ICarrierServicesConfigurator
{
    public static nint LastHost;
    public static string? LastDirectory;
    public static bool BootstrapRan;

    public void Configure(ICarrierServiceContext context)
    {
        LastHost = context.HostHandle;
        LastDirectory = context.CarrierDirectory;
        BootstrapRan = true;
        context.Services.AddSingleton(new ProbeCarrierService(context.HostHandle));
    }
}

/// <summary>一次性服务：断言换代载体容器时旧单例被释放。</summary>
public sealed class DisposableProbe : IDisposable
{
    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}

/// <summary>只实现异步释放的服务：断言换代容器时不会被同步 Dispose 卡住。</summary>
public sealed class AsyncDisposableProbe : IAsyncDisposable
{
    public bool Disposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>运行期组件阶段的检验：记录收到的是总线与日志，按需在启动/停止里挂住。</summary>
public sealed class ProbeCarrierComponent : ICarrierComponent
{
    public static int Started;
    public static int Stopped;
    public static bool HangOnStop;
    public static IEventBus? SeenBus;
    public static bool SawBootstrap;
    public static IServiceProvider? SeenCarrierServices;

    public ValueTask StartAsync(ICarrierComponentContext context, CancellationToken token)
    {
        SeenBus = context.Events;
        SawBootstrap = ProbeConfigurator.BootstrapRan;
        SeenCarrierServices = context.CarrierServices;
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
