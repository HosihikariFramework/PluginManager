using Hosihikari;
using Hosihikari.Bindings;
using Microsoft.Extensions.Logging;

[assembly: EntryPoint<Foo.FooPlugin>]

namespace Foo;

public sealed class FooPlugin : IPlugin
{
    public ValueTask OnLoad(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("OnLoad  package={Package}", context.PackageDirectory);

        // 取载体对象是自愿的逃生舱（ADR-0019）。能转型成功，说明绑定程序集的身份也走共享层。
        var seen = context.CarrierServices.GetService(typeof(ICarrierInfo)) is ICarrierInfo carrier
            ? carrier.Describe()
            : "取不到";

        File.WriteAllText(Path.Combine(context.DataDirectory, "carrier.txt"), seen);

        // 以契约类型为键登记自己，给别的插件取（ADR-0027）。IPlugin 来自共享层，
        // 所以两个 ALC 看到的是同一个类型对象。
        context.PluginServices.Register<IPlugin>(this);

        // 探针：Foo 这边的 Lib 是哪一份（ADR-0026）。
        var lib = typeof(Lib.Marker).Assembly;
        File.WriteAllText(
            Path.Combine(context.DataDirectory, "lib.txt"),
            $"{lib.Location} | {System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(lib)?.Name}");

        // 配置按「包内默认 → 包外覆盖」叠好交过来（ADR-0018）。
        File.WriteAllText(
            Path.Combine(context.DataDirectory, "greeting.txt"),
            context.Configuration["greeting"] ?? "（没有）");

        // 一个 handler 故意抛，另一个照常收到 —— 隔离到 handler（ADR-0014）。
        context.Events.Subscribe<string>(_ => throw new InvalidOperationException("故意抛出"));
        context.Events.Subscribe<string>(payload =>
            File.WriteAllText(Path.Combine(context.DataDirectory, "heard.txt"), payload));

        // 调度器跑在线程池上（ADR-0007）。
        context.Scheduler.ScheduleOnce(TimeSpan.FromMilliseconds(50), () =>
            File.WriteAllText(Path.Combine(context.DataDirectory, "scheduled.txt"), "ran"));

        // 这两个句柄**故意不保存、不退订**：框架应当在卸载时自己收回（ADR-0004）。
        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginHookResult> OnEnable(CancellationToken cancellationToken)
    {
        Console.WriteLine("[Foo] OnEnable");
        return ValueTask.FromResult(PluginHookResult.Allow());
    }

    public ValueTask<PluginHookResult> OnDisable(CancellationToken cancellationToken)
    {
        Console.WriteLine("[Foo] OnDisable");
        return ValueTask.FromResult(PluginHookResult.Allow());
    }

    public ValueTask OnUnload(CancellationToken cancellationToken)
    {
        Console.WriteLine("[Foo] OnUnload");
        return ValueTask.CompletedTask;
    }
}
