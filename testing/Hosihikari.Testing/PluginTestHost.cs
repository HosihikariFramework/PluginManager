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
    public IReadOnlyList<string> HookLog => _hooks;

    public void Load()
    {
        Plugin.OnLoad(Context, CancellationToken.None).GetAwaiter().GetResult();
        _hooks.Add("load");
    }

    public PluginHookResult Enable()
    {
        var result = Plugin.OnEnable(CancellationToken.None).GetAwaiter().GetResult();
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
        var result = Plugin.OnDisable(CancellationToken.None).GetAwaiter().GetResult();
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
            Plugin.OnUnload(CancellationToken.None).GetAwaiter().GetResult();
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
