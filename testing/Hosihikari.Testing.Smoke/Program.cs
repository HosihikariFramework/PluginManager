using Hosihikari;
using Hosihikari.Testing;

var checks = new List<(string Name, bool Passed, string Detail)>();

var invalidHostRejected = false;
try
{
    _ = new PluginTestHost(new ProbePlugin(), "../escape");
}
catch (ArgumentException)
{
    invalidHostRejected = true;
}

var probe = new ProbePlugin();
using (var host = new PluginTestHost(probe, "Probe"))
{
    host.RunLifecycle();
    checks.Add(("测试宿主拒绝越界的插件名", invalidHostRejected, invalidHostRejected ? "已拒绝" : "（没有拒绝）"));
    checks.Add((
        "生命周期按 Load → Enable → Disable → Unload 驱动",
        host.HookLog.SequenceEqual(["load", "enable", "disable", "unload"]),
        string.Join(" → ", host.HookLog)));
    checks.Add((
        "上下文给出包目录与数据目录",
        Directory.Exists(host.Context.PackageDirectory) && Directory.Exists(host.Context.DataDirectory),
        host.Context.PackageDirectory));
    checks.Add(("插件拿到上下文", probe.Context is not null, probe.Context?.Name ?? "（没有）"));

    var seen = 0;
    var subscription = host.Context.TestEvents.Subscribe<string>(_ => seen++);
    host.Context.TestEvents.Publish("first");
    host.Context.TestEvents.Publish("second");
    subscription.Dispose();
    host.Context.TestEvents.Publish("after-dispose");
    checks.Add(("事件总线订阅与退订", seen == 2, $"seen={seen}"));

    var once = 0;
    host.Context.TestScheduler.ScheduleOnce(TimeSpan.Zero, () => once++);
    host.Context.TestScheduler.RunOnce();
    checks.Add(("一次性调度手动执行", once == 1 && host.Context.TestScheduler.PendingOnce == 0, $"once={once}"));

    var periodic = 0;
    var cancel = host.Context.TestScheduler.SchedulePeriodic(TimeSpan.Zero, () => periodic++);
    host.Context.TestScheduler.RunPeriodic();
    host.Context.TestScheduler.RunPeriodic();
    cancel.Dispose();
    host.Context.TestScheduler.RunPeriodic();
    checks.Add(("周期调度可取消", periodic == 2, $"periodic={periodic}"));

    var services = host.Context.TestServices;
    var first = services.Register<IProbeService>(new ProbeService("first"));
    var duplicate = services.Register<IProbeService>(new ProbeService("second"));
    checks.Add((
        "服务注册先到先得",
        first && !duplicate && services.Get<IProbeService>()?.Name == "first",
        services.Get<IProbeService>()?.Name ?? "（没有）"));

    host.Context.ReportFault("probe fault");
    checks.Add(("故障报告可检查", host.Context.Faults.Contains("probe fault"), string.Join("；", host.Context.Faults)));
}

var refused = new RefusePlugin();
using (var host = new PluginTestHost(refused))
{
    host.Load();
    var result = host.Enable();
    checks.Add((
        "拒绝启用保留原因",
        !result.Allowed && result.Reason == "not now",
        result.Reason ?? "（没有原因）"));
}

var gate = new GatePlugin();
using (var host = new PluginTestHost(gate))
{
    host.Load();
    host.Context.Events.Publish("before-enable");
    host.Enable();
    host.Context.Events.Publish("enabled");
    host.Context.TestScheduler.RunOnce();
    host.Disable();
    host.Context.Events.Publish("disabled");
    host.Unload();
    host.Context.Events.Publish("unloaded");

    checks.Add((
        "启用闸门拦住启用前、停用后与卸载后的回调",
        gate.Seen == 1 && gate.Scheduled == 1,
        $"seen={gate.Seen} scheduled={gate.Scheduled}"));
    checks.Add((
        "卸载释放插件登记的服务",
        host.Context.TestServices.Get<IProbeService>() is null,
        host.Context.TestServices.Get<IProbeService>()?.Name ?? "（已释放）"));
}

var thrower = new ThrowUnloadPlugin();
using (var host = new PluginTestHost(thrower))
{
    host.Load();
    host.Enable();
    var threw = false;
    try
    {
        host.Unload();
    }
    catch (InvalidOperationException)
    {
        threw = true;
    }

    checks.Add((
        "OnUnload 抛出仍释放作用域",
        threw && host.Context.TestServices.Get<IProbeService>() is null,
        $"threw={threw} service={host.Context.TestServices.Get<IProbeService>()?.Name ?? "已释放"}"));
}

var unloadPublish = new UnloadPublishPlugin();
using (var host = new PluginTestHost(unloadPublish))
{
    host.Load();
    host.Enable();
    host.Unload();
    checks.Add((
        "直接卸载时先关闸门再调 OnUnload",
        unloadPublish.Seen == 0,
        $"seen={unloadPublish.Seen}"));
}

using (var harness = new PluginManagerHarness())
{
    var escaped = false;
    try
    {
        harness.CreatePackage("../escape", "{}");
    }
    catch (ArgumentException)
    {
        escaped = true;
    }

    checks.Add(("管理器辅助拒绝越界的包名", escaped, escaped ? "已拒绝" : "（没有拒绝）"));

    harness.Manager.RegisterLoader(new ProbeLoader());
    harness.CreatePackage(
        "HarnessProbe",
        """
        {
          "name": "HarnessProbe",
          "entry": "HarnessProbe.dll",
          "type": "hosihikari",
          "version": "1.0.0",
          "extraInfo": { "api_version": "2", "manifest_version": "1", "loader": "probe" }
        }
        """);
    var loaded = harness.Load("HarnessProbe");
    var enabled = harness.Manager.Enable("HarnessProbe");
    var unloaded = harness.Manager.Unload("HarnessProbe");
    checks.Add((
        "真实管理器辅助加载、启用并卸载",
        loaded && enabled && unloaded.Released,
        $"loaded={loaded} enabled={enabled} unloaded={unloaded.Released} {unloaded.Error}"));
}

Console.WriteLine("=== Hosihikari.Testing 冒烟 ===");
foreach (var (name, passed, detail) in checks)
{
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}  （{detail}）");
}

return checks.All(check => check.Passed) ? 0 : 1;

interface IProbeService
{
    string Name { get; }
}

sealed class ProbeService(string name) : IProbeService
{
    public string Name { get; } = name;
}

sealed class ProbePlugin : IPlugin
{
    public IPluginContext? Context { get; private set; }

    public void OnLoad(IPluginContext context) => Context = context;
    public PluginHookResult OnEnable() => PluginHookResult.Allow();
    public PluginHookResult OnDisable() => PluginHookResult.Allow();
    public void OnUnload() { }
}

sealed class RefusePlugin : IPlugin
{
    public void OnLoad(IPluginContext context) { }
    public PluginHookResult OnEnable() => PluginHookResult.Refuse("not now");
    public PluginHookResult OnDisable() => PluginHookResult.Allow();
    public void OnUnload() { }
}

sealed class GatePlugin : IPlugin
{
    public int Seen { get; private set; }
    public int Scheduled { get; private set; }

    public void OnLoad(IPluginContext context)
    {
        context.Events.Subscribe<string>(_ => Seen++);
        context.Scheduler.ScheduleOnce(TimeSpan.Zero, () => Scheduled++);
        context.PluginServices.Register<IProbeService>(new ProbeService("from-plugin"));
    }

    public PluginHookResult OnEnable() => PluginHookResult.Allow();
    public PluginHookResult OnDisable() => PluginHookResult.Allow();
    public void OnUnload() { }
}

sealed class UnloadPublishPlugin : IPlugin
{
    private IPluginContext? _context;
    public int Seen { get; private set; }

    public void OnLoad(IPluginContext context)
    {
        _context = context;
        context.Events.Subscribe<string>(_ => Seen++);
    }

    public PluginHookResult OnEnable() => PluginHookResult.Allow();
    public PluginHookResult OnDisable() => PluginHookResult.Allow();
    public void OnUnload() => _context?.Events.Publish("during-unload");
}

sealed class ThrowUnloadPlugin : IPlugin
{
    public void OnLoad(IPluginContext context) =>
        context.PluginServices.Register<IProbeService>(new ProbeService("throw-unload"));

    public PluginHookResult OnEnable() => PluginHookResult.Allow();
    public PluginHookResult OnDisable() => PluginHookResult.Allow();
    public void OnUnload() => throw new InvalidOperationException("unload failed");
}

sealed class ProbeLoader : IPluginLoader
{
    public string Id => "probe";
    public bool CanLoad(PluginManifest manifest) => manifest.Loader == Id;

    public PluginHandle? Load(PluginManifest manifest, string packageDirectory, IPluginContext context) =>
        new ProbeHandle(new ProbePlugin());
}

sealed class ProbeHandle(ProbePlugin plugin) : PluginHandle(plugin)
{
    private bool _unloaded;

    public override bool IsUnloaded => _unloaded;

    public override void Unload()
    {
        _unloaded = true;
        Plugin = null;
    }

    public override Task<bool> WaitForUnloadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}
