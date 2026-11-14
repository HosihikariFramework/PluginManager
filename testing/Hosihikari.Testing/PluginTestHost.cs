using Hosihikari;

namespace Hosihikari.Testing;

/// <summary>
/// 直接驱动 <see cref="IPlugin"/> 四个钩子的轻量测试宿主。它不创建 ALC、不读清单，也不模拟载体；
/// 需要真实发现、依赖与卸载时用真实的 <c>PluginManager</c>。
/// </summary>
public sealed class PluginTestHost : IDisposable
{
    private readonly string _root;
    private readonly List<string> _hooks = [];

    public PluginTestHost(IPlugin plugin, string name = "TestPlugin")
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Plugin = plugin;
        _root = Path.Combine(Path.GetTempPath(), "hosihikari-testing", Guid.NewGuid().ToString("N"));
        Context = new TestPluginContext(name, _root);
    }

    public IPlugin Plugin { get; }
    public TestPluginContext Context { get; }

    /// <summary>
    /// 裸总线，绕过启用闸门。断言分发顺序用它：<see cref="TestPluginContext.Events"/> 是带闸门的视图，
    /// 未启用时什么都不投递，测不出档位。
    /// </summary>
    public TestEventBus Bus => Context.TestEvents;

    public IReadOnlyList<string> HookLog => _hooks;

    public void Load()
    {
        Run(token => Plugin.OnLoad(Context, token));
        _hooks.Add("load");
    }

    public PluginHookResult Enable()
    {
        var result = Run(token => Plugin.OnEnable(token));
        if (result.Allowed)
        {
            Context.Scope.Open();
        }

        _hooks.Add(result.Allowed ? "enable" : $"enable:refused({result.Reason ?? "no reason given"})");
        return result;
    }

    public PluginHookResult Disable()
    {
        var wasEnabled = Context.Scope.Close();
        var result = Run(token => Plugin.OnDisable(token));
        if (!result.Allowed && wasEnabled)
        {
            Context.Scope.Open();
        }

        _hooks.Add(result.Allowed ? "disable" : $"disable:refused({result.Reason ?? "no reason given"})");
        return result;
    }

    public void Unload()
    {
        Context.Scope.Close();
        try
        {
            Run(token => Plugin.OnUnload(token));
        }
        finally
        {
            Context.Scope.ReleaseAll();
            _hooks.Add("unload");
        }
    }

    /// <summary>按 Load → Enable → Disable → Unload 走一遍；任一钩子拒绝时抛出并带上原因。</summary>
    public void RunLifecycle()
    {
        Load();
        var enabled = Enable();
        if (!enabled.Allowed)
        {
            throw new InvalidOperationException($"Enable refused: {enabled.Reason ?? "no reason given"}");
        }

        var disabled = Disable();
        if (!disabled.Allowed)
        {
            throw new InvalidOperationException($"Disable refused: {disabled.Reason ?? "no reason given"}");
        }

        Unload();
    }

    /// <summary>钩子投到线程池再等，与生产一致（ADR-0059）：调用方线程上若装着上下文，同步等会死锁。</summary>
    private static T Run<T>(Func<CancellationToken, ValueTask<T>> hook) =>
        Task.Run(() => hook(CancellationToken.None).AsTask()).GetAwaiter().GetResult();

    private static void Run(Func<CancellationToken, ValueTask> hook) =>
        Task.Run(() => hook(CancellationToken.None).AsTask()).GetAwaiter().GetResult();

    public void Dispose()
    {
        Context.Scope.ReleaseAll();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
