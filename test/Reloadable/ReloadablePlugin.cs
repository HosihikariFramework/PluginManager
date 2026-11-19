using Hosihikari;

[assembly: EntryPoint<Reloadable.ReloadablePlugin>]

namespace Reloadable;

/// <summary>
/// 只为重载验收存在的最小插件（FX-0062）：没有依赖者，装载后能安稳地重载。
/// 每次 OnDisable 往数据目录追加一行，供宿主断言一次重载只停一次。
/// </summary>
public sealed class ReloadablePlugin : IPlugin
{
    private string? _dataDirectory;

    public ValueTask OnLoad(IPluginContext context, CancellationToken cancellationToken)
    {
        _dataDirectory = context.DataDirectory;
        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginHookResult> OnEnable(CancellationToken cancellationToken) =>
        ValueTask.FromResult(PluginHookResult.Allow());

    public ValueTask<PluginHookResult> OnDisable(CancellationToken cancellationToken)
    {
        if (_dataDirectory is { } directory)
        {
            File.AppendAllText(Path.Combine(directory, "disable.log"), "disable" + Environment.NewLine);
        }

        return ValueTask.FromResult(PluginHookResult.Allow());
    }

    public ValueTask OnUnload(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
