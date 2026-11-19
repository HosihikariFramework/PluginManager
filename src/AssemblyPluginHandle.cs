using McMaster.NETCore.Plugins;

namespace Hosihikari.Internal;

/// <summary>内置加载器交出的句柄：放下插件并观察 collectible ALC 的回收（FX-0010 / 0023）。</summary>
internal sealed class AssemblyPluginHandle(IPlugin plugin, PluginLoader loader) : PluginHandle(plugin)
{
    private WeakReference? _weakContext;
    private PluginLoader? _loader = loader;

    public override bool IsUnloaded => _weakContext is { IsAlive: false };

    public override void Unload()
    {
        if (_loader is null)
        {
            return;
        }

        _weakContext = new WeakReference(_loader.LoadContext);
        _loader.Dispose();
        _loader = null;
        Plugin = null;
    }

    public override async Task<bool> WaitForUnloadAsync(CancellationToken cancellationToken = default)
    {
        if (_weakContext is null)
        {
            return false;
        }

        for (var i = 0; i < 100 && _weakContext.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (_weakContext.IsAlive)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }

        return !_weakContext.IsAlive;
    }
}
