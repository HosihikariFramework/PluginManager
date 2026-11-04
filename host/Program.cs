using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Hosihikari;
using Hosihikari.Bindings;
using Hosihikari.Internal;
using Hosihikari.Internal.Pack;
using Hosihikari.Spike;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

if (args.Length > 0 && args[0] == "publish"
    && Environment.GetEnvironmentVariable("HOSHI_PACK_OUTPUT_FIXTURE") == "1")
{
    Console.Out.Write(new string('o', 2 * 1024 * 1024));
    Console.Error.Write(new string('e', 2 * 1024 * 1024));
    var output = args[Array.IndexOf(args, "-o") + 1];
    Directory.CreateDirectory(output);
    return 0;
}

if (args.Length == 2 && args[0] == "--pack-output-regression")
{
    return RegressionChecks.PackOutputChild(args[1]);
}

// 断言引用操作员文本；把 UI 文化钉在 zh-Hans，让资源在每台 runner 与后台线程上解析一致。
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("zh-Hans");
CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-Hans");

// 载体中立检查（ADR-0015）：普通 .NET 控制台主机驱动核心，不碰 BDS、Shim、任何载体。
var repoRoot = RepoRoot();
var stage = Path.Combine(Path.GetTempPath(), "hosihikari-spike");
if (Directory.Exists(stage))
{
    Directory.Delete(stage, true);
}

var pluginsRoot = Path.Combine(stage, "plugins");
var dataRoot = Path.Combine(stage, "data");
var configRoot = Path.Combine(stage, "config", "hosihikari");
var reported = new List<string>();

// 目标载体的共享层清单。打包工具与运行期必须拿同一份 —— 一个来自这里，另一个来自
// AssemblyPluginLoader 的构造（ADR-0021 / 0026）。这个宿主用的是替身载体绑定。
var sharedLayer = new[] { "Hosihikari.Plugin.Abstractions.dll", "Hosihikari.Bindings.Stub.dll" };

Stage("Foo", withConfig: true);
Stage("Bar", withConfig: false);
StageBroken();
StageScript();
StageDiagnostic();
StagePinned();
StageGuarded("Guarded");
StageGuarded("Faulty");
StageGuarded("Vanishing");
StageThrowing();
StageWrongType();
StageBadFormat();
StageEscapingEntry();

// 宿主服务用现成的包拼装（ADR-0007）：DI + 日志。载体对象经服务解析交给插件（ADR-0019）。
// Loader 的可读失败原因走插件日志，所以测试同时收一份。
var providerLogs = new System.Collections.Concurrent.ConcurrentQueue<string>();
var collection = new ServiceCollection();
collection.AddLogging(builder => builder
    .AddSimpleConsole(options => options.SingleLine = true)
    .AddProvider(new CapturingLoggerProvider(providerLogs))
    .SetMinimumLevel(LogLevel.Information));
collection.AddSingleton(typeof(ICarrierInfo), new StubCarrier("stub"));
var services = collection.BuildServiceProvider();

// 运营方的覆盖放到包外（ADR-0018）。
Directory.CreateDirectory(configRoot);
File.WriteAllText(Path.Combine(configRoot, "Foo.conf"), "greeting = \"overridden\"\n");

// 故障卸载成功后宿主会回调这里；用并发包，后台线程也会写（ADR-0030）。
var removed = new System.Collections.Concurrent.ConcurrentBag<string>();
var host = new PluginManager(
    pluginsRoot,
    dataRoot,
    configRoot,
    entry =>
    {
        reported.Add(entry);
        Console.WriteLine($"[框架] {entry}");
    },
    services,
    name =>
    {
        removed.Add(name);
        Console.WriteLine($"[框架] 已移除 {name}");
    });
var pinnedLoader = new PinnedPluginLoader();
var guardedLoader = new GuardedPluginLoader();
host.RegisterLoader(new AssemblyPluginLoader(typeof(StubCarrier).Assembly));
host.RegisterLoader(new ScriptPluginLoader());
host.RegisterLoader(pinnedLoader);
host.RegisterLoader(guardedLoader);
host.RegisterLoader(new ThrowingPluginLoader());
host.RegisterLoader(new DiagnosticsPluginLoader());

Console.WriteLine("=== 加载 ===");
host.LoadAll();
Console.WriteLine($"已加载：{string.Join(", ", host.LoadedNames)}");

// 事件与调度是异步生效的，等一小会儿再看结果。
host.Events.Publish("ping");
Thread.Sleep(300);

var checks = new List<(string Name, bool Passed, string Detail)>();

// A25：同名 Loader 再登记会被拒并报出来，先来的那个不变（ADR-0009）
var duplicateRefused = !host.RegisterLoader(new ScriptPluginLoader());
checks.Add((
    "A25 同名 Loader 被拒并报错",
    duplicateRefused && reported.Any(entry => entry.Contains("插件种类名必须唯一")),
    reported.LastOrDefault(entry => entry.Contains("插件种类名必须唯一")) ?? "（没有报告）"
));

// A48：加载器诊断在 Load 之前把问题并入报告（ADR-0041）
var diagnosticProblem = reported.FirstOrDefault(
    entry => entry.Contains("Diagnostic") && entry.Contains("缺少入口文件"));
checks.Add((
    "A48 加载器诊断进入加载报告",
    diagnosticProblem is not null && !host.LoadedNames.Contains("Diagnostic"),
    diagnosticProblem ?? "（没有诊断报告）"
));

// A49：状态快照是只读视图，带版本、加载器、启用状态与依赖（ADR-0041）
var fooSnapshot = host.Plugins.FirstOrDefault(plugin => plugin.Name == "Foo");
var snapshotFound = host.TryGetPlugin("Foo", out var fooInfo)
    && fooInfo is { Name: "Foo", State: PluginState.Loaded, Enabled: true };
var snapshotOk = fooSnapshot is
{
    Version: "1.0.0",
    Loader: "assembly",
    State: PluginState.Loaded,
    Enabled: true,
}
&& fooSnapshot.Dependencies.Any(dependency => dependency.Name == PluginContractInfo.ParentName)
&& snapshotFound;
checks.Add((
    "A49 插件状态快照读出依赖与启用状态",
    snapshotOk,
    fooSnapshot is null
        ? "（没有 Foo 快照）"
        : $"{fooSnapshot.Name} {fooSnapshot.Version} {fooSnapshot.Loader} "
            + $"{fooSnapshot.State} enabled={fooSnapshot.Enabled} deps={fooSnapshot.Dependencies.Count}"
));

// A9：一个 handler 抛出，其余照常收到（ADR-0014）
var heardFile = Path.Combine(dataRoot, "Foo", "heard.txt");
var heard = File.Exists(heardFile) ? File.ReadAllText(heardFile) : "（没收到）";
checks.Add(("A9 handler 抛出不影响其余 handler", heard == "ping", heard));

// A10：调度器的一次性任务真的跑了（ADR-0007）
var scheduledFile = Path.Combine(dataRoot, "Foo", "scheduled.txt");
checks.Add(("A10 调度器的一次性任务执行", File.Exists(scheduledFile), File.Exists(scheduledFile) ? "已执行" : "没跑"));

// A40：停用关闭作用域闸门，事件不再投递；启用后恢复（ADR-0035）
var fooDisabled = host.Disable("Foo");
host.Events.Publish("after-disable");
Thread.Sleep(100);
var heardWhileDisabled = File.Exists(heardFile) ? File.ReadAllText(heardFile) : "（没收到）";
var fooEnabled = host.Enable("Foo");
host.Events.Publish("after-enable");
Thread.Sleep(100);
var heardAfterEnable = File.Exists(heardFile) ? File.ReadAllText(heardFile) : "（没收到）";
checks.Add((
    "A40 停用后不再回调，启用后恢复",
    fooDisabled && fooEnabled && heardWhileDisabled == "ping" && heardAfterEnable == "after-enable",
    $"disabled={fooDisabled} while={heardWhileDisabled} enabled={fooEnabled} after={heardAfterEnable}"
));

// A16：普通依赖（非契约、不在共享层）按 ALC 各一份 —— 这正是可卸载性的代价，
// 也是"插件之间不共享类型"的另一面（ADR-0026）。
var fooLib = File.ReadAllText(Path.Combine(dataRoot, "Foo", "lib.txt"));
var barLib = File.ReadAllText(Path.Combine(dataRoot, "Bar", "lib.txt"));
checks.Add((
    "A16 普通依赖按 ALC 各一份",
    fooLib != barLib,
    $"Foo: {fooLib} ／ Bar: {barLib}"
));

// A21：插件之间只走通道 —— 用共享层里的类型登记与解析，两个 ALC 看到同一个类型对象；
// 同类型再次登记被拒、先来的留着（ADR-0009 / 0026 / 0027）。
// 收回这条不单独断言：登记没被摘掉的话，Foo 的 ALC 就被钉住，下面那条卸载断言会失败。
var peerFile = Path.Combine(dataRoot, "Bar", "peer.txt");
var peer = File.Exists(peerFile) ? File.ReadAllText(peerFile) : "（插件没写出东西）";
checks.Add((
    "A21 跨插件服务登记与解析",
    peer == "Foo.FooPlugin | 冲突登记=False",
    peer
));

// A11：两个测试插件都经打包工具装配成了包
var packedOk = Directory.Exists(Path.Combine(pluginsRoot, "Foo")) && Directory.Exists(Path.Combine(pluginsRoot, "Bar"));
checks.Add(("A11 打包工具产出包", packedOk, packedOk ? "Foo / Bar" : "缺包"));

// A12：误把契约打进包里的构建输出，打包必须拦下（ADR-0004 / 0019）—— 这条在运行期是静默的
var badSource = Path.Combine(stage, "bad-source");
Directory.CreateDirectory(badSource);
File.Copy(
    Path.Combine(AppContext.BaseDirectory, "Hosihikari.Plugin.Abstractions.dll"),
    Path.Combine(badSource, "Hosihikari.Plugin.Abstractions.dll"),
    overwrite: true);
var bad = Packager.Pack(badSource, Path.Combine(repoRoot, "test", "Foo", "manifest.json"), Path.Combine(stage, "bad-out"), sharedLayer);
var sharedProblem = bad.Problems.FirstOrDefault(problem => problem.Contains("共享层"));
checks.Add(("A12 打包拒绝共享层副本", !bad.Succeeded && sharedProblem is not null, sharedProblem ?? "（未拦下）"));

// A6：api_version 不匹配必须被拒载并留下可读报告（ADR-0012）
var rejected = !host.LoadedNames.Contains("Broken") && reported.Any(entry => entry.Contains("api_version 不匹配"));
checks.Add(("A6 api_version 不匹配时拒载", rejected, reported.LastOrDefault(entry => entry.Contains("api_version 不匹配")) ?? "（无报告）"));

// A33：manifest_version 不匹配必须被拒载（ADR-0012）
var formatRefused = !host.LoadedNames.Contains("BadFormat")
    && reported.Any(entry => entry.Contains("manifest_version 不匹配"));
checks.Add((
    "A33 manifest_version 不匹配时拒载",
    formatRefused,
    reported.LastOrDefault(entry => entry.Contains("manifest_version")) ?? "（无报告）"
));

// A34：entry 必须留在包内 —— 绝对路径与 .. 都不能把包外的东西装进插件的 ALC（ADR-0006）
var escapingRefused = !host.LoadedNames.Contains("Escaping")
    && reported.Any(entry => entry.Contains("entry 必须是包内相对路径"));
checks.Add((
    "A34 entry 出界时拒载",
    escapingRefused,
    reported.LastOrDefault(entry => entry.Contains("entry 必须是包内相对路径")) ?? "（无报告）"
));

// A5：载体绑定程序集也走共享层 —— 转型成功才拿得到，副本会拿到另一种身份（ADR-0019）
var carrierFile = Path.Combine(dataRoot, "Foo", "carrier.txt");
var seen = File.Exists(carrierFile) ? File.ReadAllText(carrierFile) : "（插件没写出东西）";
checks.Add(("A5 载体绑定也走共享层", seen == "stub-carrier:stub", seen));

// A8：包内默认被包外覆盖，且没写配置的插件照样加载（ADR-0018）
var greetingFile = Path.Combine(dataRoot, "Foo", "greeting.txt");
var greeting = File.Exists(greetingFile) ? File.ReadAllText(greetingFile) : "（插件没写出东西）";
checks.Add(("A8 配置分层：包外覆盖包内", greeting == "overridden" && host.LoadedNames.Contains("Bar"), greeting));

// A7：另一种插件类型（不碰 ALC）走的是同一套契约与同一套生命周期（ADR-0002 / ADR-0010）
var ranFile = Path.Combine(dataRoot, "Lua", "ran.txt");
checks.Add(("A7 非 ALC 的插件类型走同一契约", File.Exists(ranFile), File.Exists(ranFile) ? "脚本已执行" : "脚本没跑"));

// A4：Bar 依赖 Foo，此时卸 Foo 必须被拒，且如实返回（ADR-0004）
var refused = host.Unload("Foo");
checks.Add(("A4 有依赖者时拒绝卸载", !refused.Released && !string.IsNullOrEmpty(refused.Error), refused.Error ?? "（未拒绝）"));

// A24：传递依赖的第二段 —— Bar 现在依赖者是 Lua，卸它同样要被拒（ADR-0004）
var barRefused = host.Unload("Bar");
checks.Add((
    "A24 传递依赖上仍然拒绝卸载",
    !barRefused.Released && barRefused.Error?.Contains("Lua") == true,
    barRefused.Error ?? "（未拒绝）"
));

// A13：父级态下父级把包逐个交过来（ADR-0003）—— 这个包不在 plugins/ 下，只有交付能让它进来
var delivered = host.Load(Path.Combine(stage, "parented", "Pinned"));
checks.Add((
    "A13 父级交付的包装上了",
    delivered && host.LoadedNames.Contains("Pinned"),
    delivered ? string.Join(", ", host.LoadedNames) : "交付被拒"
));

// A20：父级态下启停由父级驱动（ADR-0003）—— 交付时不自动启用，父级一发话钩子就该到
var hookFile = Path.Combine(dataRoot, "Pinned", "hook.txt");
var afterDelivery = File.Exists(hookFile) ? File.ReadAllText(hookFile) : "（没调过）";
var disabled = host.Disable("Pinned");
var afterDisable = File.ReadAllText(hookFile);
var enabled = host.Enable("Pinned");
var afterEnable = File.ReadAllText(hookFile);
checks.Add((
    "A20 父级驱动启停，交付时不自动启用",
    afterDelivery == "loaded" && disabled && afterDisable == "disabled" && enabled && afterEnable == "enabled",
    $"交付后 {afterDelivery} → 停用 {afterDisable} → 启用 {afterEnable}"
));

// A14：放下之后回收验证没过，就是没卸掉 —— 照实报失败（ADR-0004）
var pinnedFirst = host.Unload("Pinned");
checks.Add((
    "A14 回收没过时如实报失败",
    !pinnedFirst.Released && pinnedFirst.Error?.Contains("回收") == true,
    pinnedFirst.Error ?? "（居然说卸掉了）"
));

// A15：再问一次是重新验证，不是"未加载"；外面那根没了就真卸掉
pinnedLoader.Pinned = false;
var pinnedSecond = host.Unload("Pinned");
checks.Add((
    "A15 待回收的插件可以再卸一次",
    pinnedSecond.Released && !host.LoadedNames.Contains("Pinned"),
    pinnedSecond.Error ?? "已回收"
));

// A47：并发卸载共享同一个进行中的回收验证，验证等待不占 _gate。
pinnedLoader.Pinned = true;
var concurrentLoaded = host.Load(Path.Combine(stage, "parented", "Pinned"));
var waitsBefore = pinnedLoader.WaitCalls;
pinnedLoader.WaitStarted.Reset();
var concurrentFirst = Task.Run(() => host.Unload("Pinned"));
var firstWaiting = pinnedLoader.WaitStarted.Wait(TimeSpan.FromSeconds(2));
var concurrentSecond = Task.Run(() => host.Unload("Pinned"));
Thread.Sleep(300);
pinnedLoader.Pinned = false;
var concurrentFinished = Task.WaitAll(
    new[] { concurrentFirst, concurrentSecond },
    TimeSpan.FromSeconds(5));
var concurrentReleased = concurrentFinished
    && concurrentFirst.Result.Released
    && concurrentSecond.Result.Released;
checks.Add((
    "A47 并发卸载共享同一个回收验证",
    concurrentLoaded && firstWaiting && concurrentReleased
        && pinnedLoader.WaitCalls - waitsBefore == 1
        && !host.LoadedNames.Contains("Pinned"),
    $"loaded={concurrentLoaded} waiting={firstWaiting} released={concurrentReleased} "
        + $"waits={pinnedLoader.WaitCalls - waitsBefore}"
));

// A50：回收验证超时按失败记，槽位留在待回收；下一次卸载复用同一个验证任务（ADR-0042）。
var timeoutLoader = new PinnedPluginLoader { Pinned = true };
var timeoutHost = new PluginManager(
    pluginsRoot,
    Path.Combine(stage, "timeout", "data"),
    Path.Combine(stage, "timeout", "config"),
    _ => { },
    null,
    null,
    new PluginManagerOptions { UnloadVerificationTimeout = TimeSpan.FromMilliseconds(150) });
timeoutHost.RegisterLoader(timeoutLoader);
var timeoutLoaded = timeoutHost.Load(Path.Combine(stage, "parented", "Pinned"));
var timeoutFirst = timeoutHost.Unload("Pinned");
var timeoutDetached = timeoutHost.TryGetPlugin("Pinned", out var timeoutInfo)
    && timeoutInfo is { State: PluginState.Detached, Enabled: false };
timeoutLoader.Pinned = false;
var timeoutSecond = timeoutHost.Unload("Pinned");
var timeoutGone = !timeoutHost.TryGetPlugin("Pinned", out _);
checks.Add((
    "A50 回收验证超时按失败记并保留待回收",
    timeoutLoaded && !timeoutFirst.Released
        && timeoutFirst.Error?.Contains("回收验证超时") == true
        && timeoutDetached && timeoutSecond.Released && timeoutGone
        && timeoutLoader.WaitCalls == 1,
    $"loaded={timeoutLoaded} first={timeoutFirst.Error ?? "成功"} detached={timeoutDetached} "
        + $"second={timeoutSecond.Released} waits={timeoutLoader.WaitCalls}"
));

// A51：WaitAsync 计时器不支持的超时在构造期拒绝，避免每次卸载都失败且不验证。
var invalidTimeoutRefused = false;
try
{
    _ = new PluginManager(
        pluginsRoot,
        Path.Combine(stage, "invalid-timeout", "data"),
        Path.Combine(stage, "invalid-timeout", "config"),
        _ => { },
        null,
        null,
        new PluginManagerOptions { UnloadVerificationTimeout = TimeSpan.FromDays(60) });
}
catch (ArgumentOutOfRangeException)
{
    invalidTimeoutRefused = true;
}

checks.Add((
    "A51 拒绝超出计时器范围的卸载超时",
    invalidTimeoutRefused,
    invalidTimeoutRefused ? "构造期已拒绝" : "（没有拒绝）"
));

// A29：拒绝启用留在册、未启用（ADR-0030）
var guardedLoaded = host.Load(Path.Combine(stage, "parented", "Guarded"));
guardedLoader.RefuseEnable = true;
var enableRefused = !host.Enable("Guarded") && host.LoadedNames.Contains("Guarded");
guardedLoader.RefuseEnable = false;
var enableAllowed = host.Enable("Guarded");
guardedLoader.RefuseEnable = true;
var enableRepeated = host.Enable("Guarded");
guardedLoader.RefuseEnable = false;
checks.Add((
    "A29 拒绝启用留在册、未启用；已启用时重复启用不再调钩子",
    guardedLoaded && enableRefused && enableAllowed && enableRepeated,
    $"loaded={guardedLoaded} refused={enableRefused} later={enableAllowed} repeated={enableRepeated}"
));

// A30：拒绝停用让卸载中止；允许后才真正卸掉（ADR-0030）
guardedLoader.RefuseDisable = true;
var disableRefused = !host.Disable("Guarded");
var unloadRefused = host.Unload("Guarded");
var stillLoaded = host.LoadedNames.Contains("Guarded");
guardedLoader.RefuseDisable = false;
var unloadedAfter = host.Unload("Guarded");
checks.Add((
    "A30 拒绝停用中止卸载",
    disableRefused && !unloadRefused.Released
        && unloadRefused.Error?.Contains("拒绝停用") == true && stillLoaded && unloadedAfter.Released,
    unloadRefused.Error ?? "（居然卸掉了）"
));

// A31：钩子抛异常触发故障卸载，并通知载体（ADR-0030）
var faultyLoaded = host.Load(Path.Combine(stage, "parented", "Faulty"));
var faultyEnable = host.Enable("Faulty");
checks.Add((
    "A31 钩子抛出触发故障卸载",
    faultyLoaded && !faultyEnable && !host.LoadedNames.Contains("Faulty")
        && removed.Any(name => name == "Faulty"),
    $"removed=[{string.Join(", ", removed)}]"
));

// A32：Loader 在钩子之外报告的故障走同一条路径（ADR-0030）
var vanishingLoaded = host.Load(Path.Combine(stage, "parented", "Vanishing"));
// 先启用：闸门关闭时定时器不会跑（ADR-0035）。
host.Enable("Vanishing");
Thread.Sleep(150);
checks.Add((
    "A32 钩子之外的故障也卸载并通知",
    vanishingLoaded && !host.LoadedNames.Contains("Vanishing")
        && removed.Any(name => name == "Vanishing"),
    $"removed=[{string.Join(", ", removed)}]"
));

// A43：故障报告必须先把 _gate 让出来再回调。父级线程可能正握着日志锁等 _gate，
// 报告在锁内回调就会互相等死。这里用第二个管理器复现锁序：回调里让另一个线程去 Load，
// 若 _gate 还攥在报告线程手里，那个 Load 在超时内完不成。
var probePackage = Path.Combine(stage, "parented", "Probe");
Directory.CreateDirectory(probePackage);
File.WriteAllText(Path.Combine(probePackage, "manifest.json"), """
    {
      "name": "Probe",
      "entry": "Probe.dll",
      "type": "hosihikari",
      "extraInfo": { "api_version": "2", "manifest_version": "1", "loader": "guarded" }
    }
    """);
PluginManager? probeHost = null;
var probeTimedOut = false;
probeHost = new PluginManager(
    pluginsRoot,
    Path.Combine(stage, "probe", "data"),
    Path.Combine(stage, "probe", "config"),
    entry =>
    {
        if (!entry.Contains("故障"))
        {
            return;
        }

        var loader = new Thread(() => probeHost!.Load(probePackage)) { IsBackground = true };
        loader.Start();
        if (!loader.Join(TimeSpan.FromSeconds(5)))
        {
            probeTimedOut = true;
        }
    });
probeHost.RegisterLoader(new GuardedPluginLoader());
probeHost.Load(Path.Combine(stage, "parented", "Vanishing"));
probeHost.Enable("Vanishing");
Thread.Sleep(200);
checks.Add((
    "A43 故障报告先放锁再回调",
    !probeTimedOut && probeHost.LoadedNames.Contains("Probe"),
    $"timedOut={probeTimedOut} loaded=[{string.Join(", ", probeHost.LoadedNames)}]"
));

// A44：Enable/Disable 的异常路径同样必须先把 _gate 让出来再回调。
// Faulty 的 OnEnable 抛异常；报告回调里让另一个线程去 Load，超时即说明外层锁没放。
PluginManager? enableHost = null;
var enableProbeTimedOut = false;
var enableProbeStarted = false;
enableHost = new PluginManager(
    pluginsRoot,
    Path.Combine(stage, "enable", "data"),
    Path.Combine(stage, "enable", "config"),
    entry =>
    {
        if (!entry.Contains("钩子抛出") || enableProbeStarted)
        {
            return;
        }

        enableProbeStarted = true;
        var loader = new Thread(() => enableHost!.Load(probePackage)) { IsBackground = true };
        loader.Start();
        if (!loader.Join(TimeSpan.FromSeconds(5)))
        {
            enableProbeTimedOut = true;
        }
    });
enableHost.RegisterLoader(new GuardedPluginLoader());
enableHost.Load(Path.Combine(stage, "parented", "Faulty"));
var faultyEnabled = enableHost.Enable("Faulty");
checks.Add((
    "A44 启停异常先放锁再回调",
    !faultyEnabled && !enableProbeTimedOut
        && !enableHost.LoadedNames.Contains("Faulty")
        && enableHost.LoadedNames.Contains("Probe"),
    $"enabled={faultyEnabled} timedOut={enableProbeTimedOut} loaded=[{string.Join(", ", enableHost.LoadedNames)}]"
));

// A45：故障属于抛出的那个实例。报告在锁外，期间同名插件可能已经重载 ——
// 随后按名字报故障就会把健康的新实例卸掉。这里在报告回调里完成同名重载，
// 返回后新实例必须仍在场，并且能正常启用。
PluginManager? raceHost = null;
var raceLoader = new GuardedPluginLoader();
var raceReloaded = false;
raceHost = new PluginManager(
    pluginsRoot,
    Path.Combine(stage, "race", "data"),
    Path.Combine(stage, "race", "config"),
    entry =>
    {
        if (!entry.Contains("钩子抛出") || raceReloaded)
        {
            return;
        }

        raceReloaded = true;
        raceHost!.Unload("Faulty");
        raceLoader.ThrowOnEnable = false;
        raceHost.Load(Path.Combine(stage, "parented", "Faulty"));
    });
raceHost.RegisterLoader(raceLoader);
raceHost.Load(Path.Combine(stage, "parented", "Faulty"));
var raceFaulted = raceHost.Enable("Faulty");
var raceSurvived = raceHost.LoadedNames.Contains("Faulty");
var raceReenabled = raceHost.Enable("Faulty");
checks.Add((
    "A45 故障不卸载同名新实例",
    !raceFaulted && raceReloaded && raceSurvived && raceReenabled
        && raceHost.LoadedNames.Contains("Faulty"),
    $"faulted={raceFaulted} reloaded={raceReloaded} survived={raceSurvived} "
        + $"reenabled={raceReenabled} loaded=[{string.Join(", ", raceHost.LoadedNames)}]"
));

// A46：上下文被外部保留时，故障卸载仍要能回收 ALC。
// 上下文只带名字与装载编号；带上槽位、实例或委托，外部的这一份引用就会钉住整个 ALC。
var retainedPacked = Packager.Pack(
    OutputDirectory("Retained"),
    Path.Combine(repoRoot, "test", "Retained", "manifest.json"),
    Path.Combine(stage, "parented"),
    sharedLayer);
var retainedLoaded = retainedPacked.PackageDirectory is { } retainedPackage && host.Load(retainedPackage);
var retainedFaulted = !host.Enable("Retained");
var retainedRemoved = removed.Any(name => name == "Retained");
var retainedFailure = reported.LastOrDefault(entry => entry.Contains("Retained") && entry.Contains("没完成"));
ContextSink.Held = null;
checks.Add((
    "A46 外部保留上下文仍能回收 ALC",
    retainedLoaded && retainedFaulted && retainedRemoved && retainedFailure is null,
    $"packed={retainedPacked.Succeeded} loaded={retainedLoaded} faulted={retainedFaulted} "
        + $"removed={retainedRemoved} failure={retainedFailure ?? "无"}"
));

// A17：Loader 在装载时抛出 —— 如实把**完整异常**报出来，且这一个插件没装上（ADR-0009）
var thrown = reported.FirstOrDefault(entry => entry.Contains(ThrowingPluginLoader.ThrowMessage));
var thrownFull = thrown is not null && thrown.Contains(nameof(InvalidOperationException));
checks.Add((
    "A17 装载抛出时如实报告且隔离",
    !host.LoadedNames.Contains("Throwing") && thrownFull,
    thrown ?? "（没有报告）"
));

// A18：清单 type 不是 hosihikari 的包必须被拒 —— 父级态下它根本到不了我们手里（ADR-0006）
var typeRefused = !host.LoadedNames.Contains("WrongType")
    && reported.Any(entry => entry.Contains("type 必须是"));
checks.Add((
    "A18 type 写错时拒载",
    typeRefused,
    reported.LastOrDefault(entry => entry.Contains("type 必须是")) ?? "（未拒载）"
));

// A19：打包期同样要拦 —— 写错的包在父级那边连路由都过不去，失败点离病因很远（ADR-0006）
var wrongTypeManifest = Path.Combine(stage, "wrong-type.json");
File.WriteAllText(
    wrongTypeManifest,
    File.ReadAllText(Path.Combine(repoRoot, "test", "Foo", "manifest.json"))
        .Replace("\"type\": \"hosihikari\"", "\"type\": \"assembly\""));
var wrongType = Packager.Pack(
    OutputDirectory("Foo"), wrongTypeManifest, Path.Combine(stage, "wrong-type-out"), sharedLayer);
var typeProblem = wrongType.Problems.FirstOrDefault(problem => problem.Contains("type 必须是"));
checks.Add((
    "A19 打包拒绝 type 写错的清单",
    !wrongType.Succeeded && typeProblem is not null,
    typeProblem ?? "（未拦下）"
));

// A22：entry 指向的文件不在构建输出里 —— 报错点离病因很远，所以拦在打包期（ADR-0008）
var missingEntry = Packager.Pack(
    OutputDirectory("Lib"),
    WriteManifest("missing-entry", ManifestLike("Lib", "Nope.dll")),
    Path.Combine(stage, "missing-entry-out"),
    sharedLayer);
var missingProblem = missingEntry.Problems.FirstOrDefault(problem => problem.Contains("不在构建输出里"));
checks.Add((
    "A22 打包拒绝不存在的入口",
    !missingEntry.Succeeded && missingProblem is not null,
    missingProblem ?? "（未拦下）"
));

// A23：入口特性不是恰好一个 —— Lib 一个都没声明
var noEntry = Packager.Pack(
    OutputDirectory("Lib"),
    WriteManifest("no-entry", ManifestLike("Lib", "Lib.dll")),
    Path.Combine(stage, "no-entry-out"),
    sharedLayer);
var noEntryProblem = noEntry.Problems.FirstOrDefault(problem => problem.Contains("个入口"));
checks.Add((
    "A23 打包拒绝没有入口声明的程序集",
    !noEntry.Succeeded && noEntryProblem is not null,
    noEntryProblem ?? "（未拦下）"
));

// A35：入口程序集没有声明入口时，Loader 报出数量（ADR-0008）
var noEntryPackage = Path.Combine(stage, "parented", "NoEntry");
Directory.CreateDirectory(noEntryPackage);
File.Copy(Path.Combine(OutputDirectory("Lib"), "Lib.dll"), Path.Combine(noEntryPackage, "Lib.dll"), overwrite: true);
File.WriteAllText(Path.Combine(noEntryPackage, "manifest.json"), """
    {
      "name": "NoEntry",
      "entry": "Lib.dll",
      "type": "hosihikari",
      "extraInfo": { "api_version": "2", "manifest_version": "1", "loader": "assembly" }
    }
    """);
var noEntryLoaded = host.Load(noEntryPackage);
checks.Add((
    "A35 入口程序集没有入口声明时报出数量",
    !noEntryLoaded && providerLogs.Any(line => line.Contains("声明了 0 个入口")),
    providerLogs.LastOrDefault(line => line.Contains("个入口")) ?? "（无报告）"
));

// A36：入口文件不在包里时，Loader 报出路径而不是文件异常（ADR-0008）
var missingEntryPackage = Path.Combine(stage, "parented", "NoEntryFile");
Directory.CreateDirectory(missingEntryPackage);
File.WriteAllText(Path.Combine(missingEntryPackage, "manifest.json"), """
    {
      "name": "NoEntryFile",
      "entry": "Nope.dll",
      "type": "hosihikari",
      "extraInfo": { "api_version": "2", "manifest_version": "1", "loader": "assembly" }
    }
    """);
var missingEntryLoaded = host.Load(missingEntryPackage);
checks.Add((
    "A36 入口文件不在包里时报出路径",
    !missingEntryLoaded && providerLogs.Any(line => line.Contains("不在包里")),
    providerLogs.LastOrDefault(line => line.Contains("不在包里")) ?? "（无报告）"
));

// A37：打包期同样拦 manifest_version（ADR-0012）
var badFormatManifest = WriteManifest("bad-format", """
    {
      "name": "Foo",
      "entry": "Foo.dll",
      "type": "hosihikari",
      "extraInfo": { "api_version": "2", "manifest_version": "9" }
    }
    """);
var badFormat = Packager.Pack(
    OutputDirectory("Foo"), badFormatManifest, Path.Combine(stage, "bad-format-out"), sharedLayer);
var badFormatProblem = badFormat.Problems.FirstOrDefault(problem => problem.Contains("manifest_version"));
checks.Add((
    "A37 打包拒绝 manifest_version 不匹配",
    !badFormat.Succeeded && badFormatProblem is not null,
    badFormatProblem ?? "（未拦下）"
));

// A38：打包期 entry 不能出界（ADR-0006）
var escapingManifest = WriteManifest("escaping-entry", ManifestLike("Lib", "../Lib.dll"));
var escaping = Packager.Pack(
    OutputDirectory("Lib"), escapingManifest, Path.Combine(stage, "escaping-entry-out"), sharedLayer);
var escapingProblem = escaping.Problems.FirstOrDefault(problem => problem.Contains("包内相对路径"));
checks.Add((
    "A38 打包拒绝出界的入口",
    !escaping.Succeeded && escapingProblem is not null,
    escapingProblem ?? "（未拦下）"
));

// A41：输出目录落在构建输出里时拒绝，避免递归复制自我嵌套（ADR-0034）
var nested = Packager.Pack(
    OutputDirectory("Lib"),
    WriteManifest("nested-output", ManifestLike("Lib", "Lib.dll")),
    Path.Combine(OutputDirectory("Lib"), "pack-out"),
    sharedLayer);
var nestedProblem = nested.Problems.FirstOrDefault(problem => problem.Contains("打包输出"));
checks.Add((
    "A41 拒绝嵌套的打包输出",
    !nested.Succeeded && nestedProblem is not null,
    nestedProblem ?? "（未拦下）"
));

// A42：清单 name 必须是单个目录名（不能带路径分隔符）
var badNameManifest = WriteManifest("bad-name", """
    {
      "name": "../Evil",
      "entry": "Lib.dll",
      "type": "hosihikari",
      "extraInfo": { "api_version": "2", "manifest_version": "1" }
    }
    """);
var badName = Packager.Pack(
    OutputDirectory("Lib"), badNameManifest, Path.Combine(stage, "bad-name-out"), sharedLayer);
var badNameProblem = badName.Problems.FirstOrDefault(problem => problem.Contains("name 必须是单个目录名"));
checks.Add((
    "A42 打包拒绝带路径的 name",
    !badName.Succeeded && badNameProblem is not null,
    badNameProblem ?? "（未拦下）"
));

// A39：本机库按 RID 目录优先解析；卸载只回收托管 ALC（ADR-0034）
var nativeProbe = RunNativeProbe();
checks.Add(("A39 本机库 RID 优先且不阻碍回收", nativeProbe.Ok, nativeProbe.Detail));

// A26：清单里声明的第三方包在打包期还原并进包（供应链留在打包那一刻）
var withPackages = WriteManifest("with-packages", """
    {
      "name": "Bar",
      "entry": "Bar.dll",
      "type": "hosihikari",
      "extraInfo": { "api_version": "2", "manifest_version": "1", "packages": "System.IO.Hashing/10.0.0" }
    }
    """);
var declared = Packager.Pack(
    OutputDirectory("Bar"), withPackages, Path.Combine(stage, "with-packages-out"), sharedLayer);
var restored = declared.PackageDirectory is null
    ? "（没打出包）"
    : (File.Exists(Path.Combine(declared.PackageDirectory, "System.IO.Hashing.dll")) ? "已还原" : "缺 dll");
checks.Add(("A26 清单声明的第三方包在打包期还原", declared.Succeeded && restored == "已还原",
    declared.Succeeded ? restored : string.Join("；", declared.Problems)));

// A1：每个插件一个 collectible ALC（ADR-0004）
var collectible = CollectibleContextCount();
checks.Add(("A1 每插件一个 collectible ALC", collectible >= 2, $"进程内 collectible ALC 数 = {collectible}"));

// A2：插件包内不得有共享层副本 —— 这是会静默失败的那条规则（ADR-0004 / ADR-0007 / ADR-0019）。
// 清单用的是给打包工具的那一份，不再另写一份：同一份事实源（ADR-0026）。
var copies = Directory.EnumerateFiles(pluginsRoot, "*.dll", SearchOption.AllDirectories)
    .Where(file => sharedLayer.Contains(Path.GetFileName(file)))
    .ToArray();
checks.Add(("A2 插件包内无共享层副本", copies.Length == 0, copies.Length == 0 ? "无副本" : string.Join(", ", copies)));

// 从链条末端往回卸：Lua 在前、Foo 在后。反序是依赖图的另一半承诺（ADR-0004）。
var lua = host.Unload("Lua");
checks.Add(("   链条末端的源码插件可卸", lua.Released, lua.Error ?? "已回收"));

// A3：卸载后真回收，而不是"停用就算卸了"（ADR-0004）
var bar = host.Unload("Bar");
checks.Add(("A3 卸载后 ALC 真回收", bar.Released, bar.Error ?? "已回收"));

// 依赖者都卸掉之后，Foo 才可卸
var foo = host.Unload("Foo");
checks.Add(("   依赖者卸掉后 Foo 可卸", foo.Released, foo.Error ?? "已回收"));

// 收尾自检：全部卸载后不应再有任何 collectible ALC（ADR-0004）。
GC.Collect();
GC.WaitForPendingFinalizers();
Console.WriteLine();
Console.WriteLine($"[诊断] 全部卸载后残留 collectible ALC = {CollectibleContextCount()}");

Console.WriteLine();
var languages = new (string Culture, string Token)[]
{
    ("en", "Loaded"),
    ("zh", "已加载"),
    ("zh-Hans", "已加载"),
    ("zh-TW", "已加载"),
    ("ar", "تم تحميل"),
    ("fr", "Chargé"),
    ("ru", "Загружено"),
    ("es", "Cargados"),
};
var missingLanguages = languages
    .Where(language =>
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language.Culture);
        return !CoreText.Get("Native_Loaded", 0, "").Contains(language.Token);
    })
    .Select(language => language.Culture)
    .ToArray();
CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-Hans");
checks.Add((
    "A52 操作员文本跟随 UI 文化",
    missingLanguages.Length == 0,
    missingLanguages.Length == 0
        ? $"6 种语言（繁中回落简中）"
        : $"缺少 {string.Join(", ", missingLanguages)}"
));

checks.AddRange(RegressionChecks.Run(Path.Combine(stage, "regressions")));
Console.WriteLine("=== 断言 ===");
foreach (var (name, passed, detail) in checks)
{
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}  （{detail}）");
}

return checks.All(check => check.Passed) ? 0 : 1;

// 测试插件走打包工具 —— 它们就是它的前两个用户，包因此与作者拿到的是同一种东西。
void Stage(string name, bool withConfig)
{
    var manifestPath = Path.Combine(repoRoot, "test", name, "manifest.json");
    var packed = Packager.Pack(OutputDirectory(name), manifestPath, pluginsRoot, sharedLayer);
    if (!packed.Succeeded)
    {
        reported.Add($"{name}: 打包失败 —— {string.Join("；", packed.Problems)}");
        return;
    }

    if (withConfig)
    {
        // 作者给的默认值，随包更新（ADR-0018）。
        File.WriteAllText(Path.Combine(packed.PackageDirectory!, $"{name}.conf"), "greeting = \"default\"\n");
    }
}

// 清单合法但没有能兑现的 api —— 测拒载路径（ADR-0012）。入口文件故意不存在：拒载要发生在载入之前。
void StageBroken()
{
    var package = Path.Combine(pluginsRoot, "Broken");
    Directory.CreateDirectory(package);
    File.WriteAllText(Path.Combine(package, "manifest.json"), $$"""
        {
          "name": "Broken",
          "entry": "Broken.dll",
          "type": "hosihikari",
          "extraInfo": { "api_version": "999", "manifest_version": "1" }
        }
        """);
}

// 另一种插件类型的包：入口是脚本文件，清单与程序集型同形 —— 入口怎么找是各自 Loader 的事。
void StageScript()
{
    var package = Path.Combine(pluginsRoot, "Lua");
    Directory.CreateDirectory(package);
    File.WriteAllText(Path.Combine(package, "main.lua"), "print('hello from script')\n");
    File.WriteAllText(Path.Combine(package, "manifest.json"), """
        {
          "name": "Lua",
          "entry": "main.lua",
          "type": "hosihikari",
          "dependencies": [ { "name": "Bar" } ],
          "extraInfo": { "api_version": "2", "manifest_version": "1", "loader": "script" }
        }
        """);
}

// 诊断加载器会拒掉这个包；入口文件故意不存在，证明装载没有开始。
void StageDiagnostic()
{
    var package = Path.Combine(pluginsRoot, "Diagnostic");
    Directory.CreateDirectory(package);
    File.WriteAllText(Path.Combine(package, "manifest.json"), """
        {
          "name": "Diagnostic",
          "entry": "Diagnostic.dll",
          "type": "hosihikari",
          "extraInfo": { "api_version": "2", "manifest_version": "1", "loader": "diagnostic" }
        }
        """);
}

// 父级态的那个包：故意放在 plugins/ **外面** —— 只有父级交付能让它进来（ADR-0003）。
void StagePinned()
{
    var package = Path.Combine(stage, "parented", "Pinned");
    Directory.CreateDirectory(package);
    File.WriteAllText(Path.Combine(package, "manifest.json"), """
        {
          "name": "Pinned",
          "entry": "Pinned.dll",
          "type": "hosihikari",
          "extraInfo": { "api_version": "2", "manifest_version": "1", "loader": "pinned" }
        }
        """);
}

// 拒绝与故障的检验包：同样放在 plugins/ 外面，由测试逐个交付（ADR-0030）。
void StageGuarded(string name)
{
    var package = Path.Combine(stage, "parented", name);
    Directory.CreateDirectory(package);
    File.WriteAllText(Path.Combine(package, "manifest.json"), $$"""
        {
          "name": "{{name}}",
          "entry": "{{name}}.dll",
          "type": "hosihikari",
          "extraInfo": { "api_version": "2", "manifest_version": "1", "loader": "guarded" }
        }
        """);
}

// 造一份只改一两处的清单，别把整份 JSON 复制到测试里。
string ManifestLike(string name, string entry) => $$"""
    {
      "name": "{{name}}",
      "entry": "{{entry}}",
      "type": "hosihikari",
      "extraInfo": { "api_version": "2", "manifest_version": "1" }
    }
    """;

string WriteManifest(string name, string json)
{
    var path = Path.Combine(stage, name + ".json");
    File.WriteAllText(path, json);
    return path;
}

// 装载时抛出的那个包：只到 Loader 为止，所以入口文件可以不存在。
void StageThrowing()
{
    var package = Path.Combine(pluginsRoot, "Throwing");
    Directory.CreateDirectory(package);
    File.WriteAllText(Path.Combine(package, "manifest.json"), """
        {
          "name": "Throwing",
          "entry": "Throwing.dll",
          "type": "hosihikari",
          "extraInfo": { "api_version": "2", "manifest_version": "1", "loader": "throwing" }
        }
        """);
}

// 清单 type 写错的那个包：别的都对，只有 type 是父级那边的另一个值。
void StageWrongType()
{
    var package = Path.Combine(pluginsRoot, "WrongType");
    Directory.CreateDirectory(package);
    File.WriteAllText(Path.Combine(package, "manifest.json"), """
        {
          "name": "WrongType",
          "entry": "WrongType.dll",
          "type": "assembly",
          "extraInfo": { "api_version": "2", "manifest_version": "1" }
        }
        """);
}

// manifest_version 不匹配的包：与 api 一样，拒载发生在载入之前（ADR-0012）。
void StageBadFormat()
{
    var package = Path.Combine(pluginsRoot, "BadFormat");
    Directory.CreateDirectory(package);
    File.WriteAllText(Path.Combine(package, "manifest.json"), """
        {
          "name": "BadFormat",
          "entry": "BadFormat.dll",
          "type": "hosihikari",
          "extraInfo": { "api_version": "2", "manifest_version": "9" }
        }
        """);
}

// entry 跑出包外的包：绝对路径与 .. 在清单读取时就被拒（ADR-0006）。
void StageEscapingEntry()
{
    var package = Path.Combine(pluginsRoot, "Escaping");
    Directory.CreateDirectory(package);
    File.WriteAllText(Path.Combine(package, "manifest.json"), """
        {
          "name": "Escaping",
          "entry": "../Outside.dll",
          "type": "hosihikari",
          "extraInfo": { "api_version": "2", "manifest_version": "1" }
        }
        """);
}

// 本机夹具：同一符号在包根与 RID 目录各放一份，只有 RID 那份是 42。RID 优先则插件读到 42。
(bool Ok, string Detail) RunNativeProbe()
{
    var compiler = FindCCompiler();
    if (compiler is null)
    {
        return (true, "（跳过：没有可用的 C 编译器）");
    }

    var output = OutputDirectory("NativeProbe");
    var fileName = OperatingSystem.IsWindows() ? "hosihikari_probe.dll"
        : OperatingSystem.IsMacOS() ? "libhosihikari_probe.dylib"
        : "libhosihikari_probe.so";
    var ridDirectory = Path.Combine(
        output,
        "runtimes",
        System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
        "native");
    Directory.CreateDirectory(ridDirectory);
    var source = Path.Combine(repoRoot, "test", "NativeProbe", "native", "probe.c");
    var ridCompile = CompileNative(compiler, source, Path.Combine(ridDirectory, fileName), "42");
    var rootCompile = CompileNative(compiler, source, Path.Combine(output, fileName), "1");
    if (!ridCompile.Ok || !rootCompile.Ok)
    {
        return (false, $"本机夹具编译失败：{ridCompile.Error} {rootCompile.Error}".Trim());
    }

    var packed = Packager.Pack(
        output,
        Path.Combine(repoRoot, "test", "NativeProbe", "manifest.json"),
        Path.Combine(stage, "parented"),
        sharedLayer);
    if (!packed.Succeeded || packed.PackageDirectory is null)
    {
        return (false, "打包失败：" + string.Join("；", packed.Problems));
    }

    if (!host.Load(packed.PackageDirectory))
    {
        return (false, "装载失败：" + (reported.LastOrDefault() ?? "（无报告）"));
    }

    var valueFile = Path.Combine(dataRoot, "NativeProbe", "native.txt");
    var value = File.Exists(valueFile) ? File.ReadAllText(valueFile).Trim() : "（没写出来）";
    var unload = host.Unload("NativeProbe");
    return (value == "42" && unload.Released, $"value={value} released={unload.Released} {unload.Error}");
}

static string? FindCCompiler()
{
    string[] candidates = OperatingSystem.IsWindows() ? ["clang"] : ["cc", "clang"];
    foreach (var candidate in candidates)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(candidate)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--version");
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
            {
                continue;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return candidate;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    return null;
}

static (bool Ok, string Error) CompileNative(string compiler, string source, string output, string value)
{
    var start = new System.Diagnostics.ProcessStartInfo(compiler)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    start.ArgumentList.Add(OperatingSystem.IsMacOS() ? "-dynamiclib" : "-shared");
    if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
    {
        start.ArgumentList.Add("-fPIC");
    }

    start.ArgumentList.Add("-o");
    start.ArgumentList.Add(output);
    start.ArgumentList.Add($"-DPROBE_VALUE={value}");
    start.ArgumentList.Add(source);
    using var process = System.Diagnostics.Process.Start(start)!;
    var outputText = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode == 0, (outputText + error).Trim());
}

// 单独一个方法：枚举 AssemblyLoadContext.All 会把枚举到的 ALC 钉在本方法帧上，
// 留在主流程里会让后面的卸载验证假失败 —— 观察者自己成了根（ADR-0023）。
[MethodImpl(MethodImplOptions.NoInlining)]
static int CollectibleContextCount()
{
    var count = 0;
    foreach (var context in AssemblyLoadContext.All)
    {
        if (context.IsCollectible)
        {
            count++;
        }
    }

    return count;
}

string OutputDirectory(string name)
{
#if DEBUG
    const string configuration = "Debug";
#else
    const string configuration = "Release";
#endif
    var bin = Path.Combine(repoRoot, "test", name, "bin");
    var assembly = Directory.EnumerateFiles(bin, $"{name}.dll", SearchOption.AllDirectories)
        .Where(file => file.Contains($"{Path.DirectorySeparatorChar}{configuration}{Path.DirectorySeparatorChar}"))
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .First();
    return Path.GetDirectoryName(assembly)!;
}

static string RepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null &&
           !File.Exists(Path.Combine(directory.FullName, "src", "Hosihikari.PluginManager.csproj")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根");
}

sealed class CapturingLoggerProvider(System.Collections.Concurrent.ConcurrentQueue<string> lines) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(lines);

    public void Dispose()
    {
    }

    private sealed class Logger(System.Collections.Concurrent.ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => lines.Enqueue(formatter(state, exception));
    }
}
