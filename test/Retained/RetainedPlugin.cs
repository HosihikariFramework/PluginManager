using Hosihikari;
using Hosihikari.Bindings;

[assembly: EntryPoint<Retained.RetainedPlugin>]

namespace Retained;

/// <summary>
/// 把上下文交到共享层，模拟父级或 Loader 在卸载后仍留着它。
/// 启用抛出让框架走故障卸载；随后 ALC 能不能回收，取决于上下文有没有间接攥住实例。
/// </summary>
public sealed class RetainedPlugin : IPlugin
{
    public ValueTask OnLoad(IPluginContext context, CancellationToken cancellationToken)
    {
        ContextSink.Held = context;
        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginHookResult> OnEnable(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("故意在启用时抛出");

    public ValueTask<PluginHookResult> OnDisable(CancellationToken cancellationToken) =>
        ValueTask.FromResult(PluginHookResult.Allow());

    public ValueTask OnUnload(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
