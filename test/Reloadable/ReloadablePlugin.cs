using Hosihikari;

[assembly: EntryPoint<Reloadable.ReloadablePlugin>]

namespace Reloadable;

/// <summary>只为重载验收存在的最小插件（ADR-0062）：没有依赖者，装载后能安稳地重载。</summary>
public sealed class ReloadablePlugin : IPlugin
{
    public ValueTask OnLoad(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<PluginHookResult> OnEnable(CancellationToken cancellationToken) =>
        ValueTask.FromResult(PluginHookResult.Allow());

    public ValueTask<PluginHookResult> OnDisable(CancellationToken cancellationToken) =>
        ValueTask.FromResult(PluginHookResult.Allow());

    public ValueTask OnUnload(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
