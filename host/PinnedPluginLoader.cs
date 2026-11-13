using Hosihikari;

namespace Hosihikari.Spike;

/// <summary>
/// 一个"外面还有根指着它"的插件：在 <see cref="Pinned"/> 放开之前，回收验证不会通过。
///
/// 真实的钉法是 ALC 之外的一个根（没退出的线程、共享层里的静态、宿主侧的静态），
/// 真实的 ALC 回收纪律由 A3 那条覆盖。这条要钉的是宿主的状态机：
/// 放下之后照实报失败、状态停在待回收，再问一次是**重新验证**而不是"未加载"。
/// </summary>
public sealed class PinnedPluginLoader : IPluginLoader
{
    /// <summary>为真时模拟"外面还有根"（ADR-0004 里作者欠的那笔账）。</summary>
    public bool Pinned { get; set; } = true;

    /// <summary>回收验证实际被调用了几次；并发卸载应共享同一个验证。</summary>
    public int WaitCalls;

    /// <summary>第一次进入回收验证时置位，供并发测试同步。</summary>
    public ManualResetEventSlim WaitStarted { get; } = new(false);

    public string Id => "pinned";

    public bool CanLoad(PluginManifest manifest) => manifest.Loader == Id;

    public PluginHandle? Load(PluginManifest manifest, string packageDirectory, IPluginContext context) =>
        new PinnedPluginHandle(new PinnedPlugin(), this);
}

internal sealed class PinnedPluginHandle(PinnedPlugin plugin, PinnedPluginLoader loader)
    : PluginHandle(plugin)
{
    private bool _unloadStarted;

    public override bool IsUnloaded => _unloadStarted && !loader.Pinned;

    /// <summary>只攥着加载器；插件实例在放下后立即松手 —— 与真实句柄只攥一个 WeakReference 同理。</summary>
    public override void Unload()
    {
        _unloadStarted = true;
        Plugin = null;
    }

    public override async Task<bool> WaitForUnloadAsync(CancellationToken cancellationToken = default)
    {
        if (!_unloadStarted)
        {
            return false;
        }

        Interlocked.Increment(ref loader.WaitCalls);
        loader.WaitStarted.Set();
        for (var i = 0; i < 100 && loader.Pinned; i++)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        return !loader.Pinned;
    }
}

internal sealed class PinnedPlugin : IPlugin
{
    private string? _dataDirectory;

    public ValueTask OnLoad(IPluginContext context, CancellationToken cancellationToken)
    {
        _dataDirectory = context.DataDirectory;
        Record("loaded");
        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginHookResult> OnEnable(CancellationToken cancellationToken)
    {
        Record("enabled");
        return ValueTask.FromResult(PluginHookResult.Allow());
    }

    public ValueTask<PluginHookResult> OnDisable(CancellationToken cancellationToken)
    {
        Record("disabled");
        return ValueTask.FromResult(PluginHookResult.Allow());
    }

    public ValueTask OnUnload(CancellationToken cancellationToken)
    {
        Record("unloaded");
        return ValueTask.CompletedTask;
    }

    /// <summary>把最后一个到达的钩子写下来 —— 断言据此看"谁在驱动"（ADR-0003）。</summary>
    private void Record(string hook)
    {
        if (_dataDirectory is not null)
        {
            File.WriteAllText(Path.Combine(_dataDirectory, "hook.txt"), hook);
        }
    }
}
