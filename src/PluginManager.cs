using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Hocon.Extensions.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Hosihikari.Internal;

namespace Hosihikari;

/// <summary>核心：发现、依赖图、加载器选型、生命周期驱动、失败隔离（ADR-0009 / 0010）。</summary>
public sealed class PluginManager
{
    private readonly string _pluginsRoot;
    private readonly string _dataRoot;
    private readonly string _configRoot;
    // Task.WaitAsync / Task.Delay 的计时器上限；超过它会抛 ArgumentOutOfRangeException。
    private static readonly TimeSpan MaxTimeout =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly TimeSpan _hookTimeout;
    private readonly TimeSpan _unloadVerificationTimeout;
    private readonly TimeSpan _componentStopTimeout;
    private readonly Action<string> _reportFault;
    private readonly IServiceProvider? _carrierServices;
    private readonly Action<string>? _onRemoved;
    private readonly object _gate = new();
    // 钩子跑在线程池上，钩子里回调管理器时拿不到 _gate（调用方正握着它等钩子）。
    // 所以在装载钩子里会用到的那两本账各有自己的锁，不与 _gate 相交。
    private readonly object _faultGate = new();
    private readonly object _loaderGate = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly IEventBus _events;
    private readonly IScheduler _scheduler;
    private readonly PluginServiceRegistry _registry = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _loading = [];
    private readonly Dictionary<Guid, PluginDiagnostic> _pendingFaults = [];
    private readonly HashSet<Guid> _loaderIds = [];
    private readonly Dictionary<Guid, List<IPluginLoader>> _registeredLoaders = [];
    private readonly List<IPluginLoader> _loaders = [];
    private readonly List<ICarrierComponent> _components = [];
    private bool _componentsStarted;

    public PluginManager(
        string pluginsRoot,
        string dataRoot,
        string configRoot,
        Action<string> reportFault,
        IServiceProvider? carrierServices = null,
        Action<string>? onRemoved = null)
        : this(
            pluginsRoot,
            dataRoot,
            configRoot,
            reportFault,
            carrierServices,
            onRemoved,
            new PluginManagerOptions())
    {
    }

    /// <summary>带显式配置的构造函数；现有构造函数保持原签名。</summary>
    public PluginManager(
        string pluginsRoot,
        string dataRoot,
        string configRoot,
        Action<string> reportFault,
        IServiceProvider? carrierServices,
        Action<string>? onRemoved,
        PluginManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // InfiniteTimeSpan 是 1.x 已有的取值，继续收（加法承诺）；有限值才走范围校验。
        _hookTimeout = ValidateTimeout(options.HookTimeout, nameof(options), "HookTimeout");
        _unloadVerificationTimeout = ValidateTimeout(
            options.UnloadVerificationTimeout, nameof(options), "UnloadVerificationTimeout");
        _componentStopTimeout = ValidateTimeout(
            options.ComponentStopTimeout, nameof(options), "ComponentStopTimeout");

        _pluginsRoot = pluginsRoot;
        _dataRoot = dataRoot;
        _configRoot = configRoot;
        _unloadVerificationTimeout = options.UnloadVerificationTimeout;
        _reportFault = reportFault;
        _carrierServices = carrierServices;
        _onRemoved = onRemoved;
        _loggerFactory = carrierServices?.GetService(typeof(ILoggerFactory)) as ILoggerFactory
                         ?? NullLoggerFactory.Instance;
        _events = new EventBus(_loggerFactory.CreateLogger("Hosihikari.Events"));
        _scheduler = new Scheduler(_loggerFactory.CreateLogger("Hosihikari.Scheduler"));

        // 两个根先建出来：父级态下不会走 LoadAll，而插件写数据、运营方放覆盖都指望它们在。
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_configRoot);
    }

    private static TimeSpan ValidateTimeout(TimeSpan value, string parameter, string name)
    {
        if (value != Timeout.InfiniteTimeSpan && (value <= TimeSpan.Zero || value > MaxTimeout))
        {
            throw new ArgumentOutOfRangeException(
                parameter,
                $"{name} must be positive and no more than about 49.7 days, or Timeout.InfiniteTimeSpan.");
        }

        return value;
    }

    /// <summary>
    /// 登记一个加载器。同名（<see cref="IPluginLoader.Id"/>）的已经在了就拒掉后一个，
    /// 并把冲突报出来（ADR-0009）—— 静默让后一个落空的话，写它的那个人会以为自己的包被接管了。
    /// </summary>
    /// <returns>登记成功为 <c>true</c>；同名的已在则为 <c>false</c>，先来的那个不变。</returns>
    public bool RegisterLoader(IPluginLoader loader)
    {
        var added = TryAddLoader(loader, out var problem);
        if (problem is not null)
        {
            _reportFault(problem);
        }

        return added;
    }

    /// <summary>
    /// 把一次登记归到具体的装载实例上；重载时据此保留加载器，不保留普通子插件。
    /// 在 <c>OnLoad</c> 里从钩子线程调用，而调用方正握着 <see cref="_gate"/> 等钩子 ——
    /// 所以这里只碰 <see cref="_loaderGate"/>，<c>IsLoader</c> 到装载收尾时再按标记结算。
    /// </summary>
    internal void MarkLoader(Guid instanceId, IPluginLoader loader)
    {
        lock (_loaderGate)
        {
            _loaderIds.Add(instanceId);
            if (!_registeredLoaders.TryGetValue(instanceId, out var registered))
            {
                _registeredLoaders[instanceId] = registered = [];
            }

            registered.Add(loader);
        }
    }

    /// <summary>加载失败时撤掉本次装载登记过的加载器，别让死包继续接管路由。</summary>
    private void RemoveLoaders(Guid instanceId)
    {
        lock (_loaderGate)
        {
            _loaderIds.Remove(instanceId);
            if (_registeredLoaders.Remove(instanceId, out var registered))
            {
                foreach (var loader in registered)
                {
                    _loaders.Remove(loader);
                }
            }
        }
    }

    /// <summary>
    /// 不向父级回报的登记。加载路径持锁时走这里，冲突文本交给加载报告统一带出去。
    /// 在 <c>OnLoad</c> 里从钩子线程调用，所以同样只碰 <see cref="_loaderGate"/> 这一本账。
    /// </summary>
    internal bool TryAddLoader(IPluginLoader loader, out string? problem)
    {
        lock (_loaderGate)
        {
            var existing = _loaders.FirstOrDefault(registered => registered.Id == loader.Id);
            if (existing is not null)
            {
                problem = CoreText.Get(
                    "Manager_LoaderDuplicate",
                    loader.Id,
                    existing.GetType().Name,
                    loader.GetType().Name);
                return false;
            }

            _loaders.Add(loader);
            problem = null;
            return true;
        }
    }

    private PluginDiagnostic? TakePendingFault(Guid instanceId)
    {
        lock (_faultGate)
        {
            return _pendingFaults.Remove(instanceId, out var diagnostic) ? diagnostic : null;
        }
    }

    private bool IsMarkedLoader(Guid instanceId)
    {
        lock (_loaderGate)
        {
            return _loaderIds.Contains(instanceId);
        }
    }

    /// <summary>装载收尾时清掉编号标记；登记本身留着，加载器不能撤销（ADR-0002）。</summary>
    private void ClearLoaderMark(Guid instanceId)
    {
        lock (_loaderGate)
        {
            _loaderIds.Remove(instanceId);
        }
    }

    private IPluginLoader[] LoaderSnapshot()
    {
        lock (_loaderGate)
        {
            return [.. _loaders];
        }
    }

    /// <summary>在场的插件名。已经放下、只等回收的那些不算（见 <see cref="PluginState"/>）。</summary>
    public IReadOnlyCollection<string> LoadedNames
    {
        get
        {
            lock (_gate)
            {
                return _slots.Where(slot => slot.Value.State == PluginState.Loaded)
                    .Select(slot => slot.Key)
                    .ToArray();
            }
        }
    }

    /// <summary>所有仍在表中的插件快照，包含待回收的那些。快照只读，不交出插件句柄（ADR-0041）。</summary>
    public IReadOnlyList<PluginInfo> Plugins
    {
        get
        {
            lock (_gate)
            {
                return _slots.Values
                    .OrderBy(slot => slot.Manifest.Name, StringComparer.Ordinal)
                    .Select(ToInfo)
                    .ToArray();
            }
        }
    }

    /// <summary>按名字取一个插件快照；待回收的插件也能取到，状态会显示为 Detached。</summary>
    public bool TryGetPlugin(string name, [NotNullWhen(true)] out PluginInfo? plugin)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(name, out var slot))
            {
                plugin = ToInfo(slot);
                return true;
            }

            plugin = null;
            return false;
        }
    }

    private static PluginInfo ToInfo(Slot slot) => new(
        slot.Manifest.Name,
        slot.Manifest.Version,
        slot.Manifest.Loader,
        slot.State,
        slot.Enabled,
        slot.IsLoader,
        slot.Manifest.Dependencies
            .Select(dependency => new PluginDependencyInfo(dependency.Name, dependency.VersionRange))
            .ToArray());

    /// <summary>载体适配器要把载体事件并进来，所以事件总线得对外可达（ADR-0007）。</summary>
    public IEventBus Events => _events;

    /// <summary>
    /// 启动载体包声明的进程级组件（ADR-0061）。在事件分发对插件开启**之前**、由载体在服务端线程上调用。
    /// 任何一个组件启动失败就返回 <see langword="false"/>，整个启动按失败处理；已经启动的照旧留着，
    /// 由调用方决定是否 <see cref="StopComponents"/>。
    /// </summary>
    /// <param name="carrierPackageDirectory">载体包目录；组件程序集从它里面装。</param>
    /// <param name="components">清单 <c>carrier.components</c> 的声明。</param>
    /// <returns>全部启动成功为 <see langword="true"/>。</returns>
    public bool StartComponents(
        string carrierPackageDirectory,
        IReadOnlyList<ManifestCarrierComponent> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (_componentsStarted)
        {
            return true;
        }

        var reports = new List<string>();

        // 组件类型住共享层（ADR-0026 / 0061），按约定名从默认上下文解析；载体包里的私有程序集按路径装。
        var assemblies = new Dictionary<string, System.Reflection.Assembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in components)
        {
            if (!assemblies.TryGetValue(component.Assembly, out var assembly))
            {
                var path = Path.Combine(carrierPackageDirectory, component.Assembly);
                try
                {
                    assemblies[component.Assembly] = assembly = File.Exists(path)
                        ? System.Reflection.Assembly.LoadFrom(path)
                        : System.Reflection.Assembly.Load(new System.Reflection.AssemblyName(
                            Path.GetFileNameWithoutExtension(component.Assembly)));
                }
                catch (Exception ex)
                {
                    reports.Add(CoreText.Get(
                        "Manager_ComponentAssemblyFailed",
                        component.Assembly,
                        ex.Demystify()));
                    continue;
                }
            }

            if (assembly.GetType(component.Type, throwOnError: false) is not { } type
                || Activator.CreateInstance(type) is not ICarrierComponent instance)
            {
                reports.Add(CoreText.Get("Manager_ComponentTypeFailed", component.Type));
                continue;
            }

            var context = new CarrierComponentContext(_events, _loggerFactory.CreateLogger(type.FullName ?? type.Name));
            if (!TryInvokeComponent(component.Type, token => instance.StartAsync(context, token), reports))
            {
                continue;
            }

            lock (_gate)
            {
                _components.Add(instance);
            }

            if (!_componentsStarted)
            {
                _componentsStarted = true;
            }
        }

        ReportAll(reports);
        lock (_gate)
        {
            _componentsStarted = true;
        }

        return reports.Count == 0;
    }

    /// <summary>
    /// 停止组件（ADR-0061）。在事件分发停止**之前**调用；逆着启动顺序停。
    /// 每个组件各用一份 <see cref="PluginManagerOptions.ComponentStopTimeout"/> 预算：
    /// 超时不再等待并记账，继续停下一个与后续退出（ADR-0063）。
    /// </summary>
    public void StopComponents()
    {
        ICarrierComponent[] components;
        lock (_gate)
        {
            components = [.. _components];
            _components.Clear();
            _componentsStarted = false;
        }

        var reports = new List<string>();
        for (var i = components.Length - 1; i >= 0; i--)
        {
            var component = components[i];
            var name = component.GetType().FullName ?? component.GetType().Name;
            TryInvokeComponent(name, component.StopAsync, reports);
        }

        ReportAll(reports);
    }

    /// <summary>
    /// 组件的启动/停止在服务端线程上调用，且调用方可能正是装 <c>SynchronizationContext</c> 的那个线程 ——
    /// 与钩子同样投到线程池再阻塞等（ADR-0059 / 0061）。
    /// </summary>
    private bool TryInvokeComponent(
        string name,
        Func<CancellationToken, ValueTask> call,
        List<string> reports)
    {
        using var budget = new CancellationTokenSource();
        if (_componentStopTimeout != Timeout.InfiniteTimeSpan)
        {
            budget.CancelAfter(_componentStopTimeout);
        }

        var task = RunOffPool(() => call(budget.Token));
        try
        {
            task.WaitAsync(budget.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !budget.IsCancellationRequested)
        {
            reports.Add(CoreText.Get("Manager_ComponentThrew", name, ex.Demystify()));
            return false;
        }
        catch (OperationCanceledException)
        {
            reports.Add(CoreText.Get("Manager_ComponentTimeout", name, _componentStopTimeout));
            return false;
        }
    }

    /// <summary>自持态：插件包就在 <c>plugins/</c> 下，由我们枚举、按依赖排序后逐个装（ADR-0018）。</summary>
    public void LoadAll()
    {
        var reports = new List<string>();
        try
        {
            lock (_gate)
            {
                foreach (var package in DependencyGraph.Resolve(ManifestReader.Scan(_pluginsRoot, reports.Add), reports.Add))
                {
                    if (_slots.ContainsKey(package.Manifest.Name))
                    {
                        continue;
                    }

                    var missing = package.Manifest.Dependencies
                        .Where(dependency => dependency.Name != PluginContractInfo.ParentName)
                        .Where(dependency => !_slots.TryGetValue(dependency.Name, out var slot)
                                             || slot.State != PluginState.Loaded)
                        .Select(dependency => dependency.Name)
                        .ToArray();
                    if (missing.Length > 0)
                    {
                        reports.Add(CoreText.Get(
                            "Manager_DependenciesNotLoaded",
                            package.Manifest.Name,
                            string.Join(", ", missing)));
                        continue;
                    }

                    LoadOne(package, enable: true, reports);
                }
            }
        }
        finally
        {
            ReportAll(reports);
        }
    }

    /// <summary>
    /// 父级态：父级把包逐个交过来（ADR-0003）。顺序与依赖由父级的计划保证，我们只管装。
    /// 返回它有没有装上 —— 失败原因已经进了日志。
    ///
    /// **不在这里启用**：启停由父级驱动（<see cref="IPlugin.OnDisable"/> 的约定），
    /// 父级自己的启用阶段随后会经 <see cref="Enable"/> 把钩子调到。这里先启用的话，
    /// 插件会被启用两次，而中间那段的"已启用"是父级不知道的。
    /// </summary>
    public bool Load(string packageDirectory)
    {
        var reports = new List<string>();
        var result = false;
        string? reusedLoader = null;
        try
        {
            lock (_gate)
            {
                if (ManifestReader.Read(packageDirectory, reports.Add) is not { } package)
                {
                    result = false;
                }
                else if (_slots.TryGetValue(package.Manifest.Name, out var slot))
                {
                    if (slot.IsLoader && slot.State == PluginState.Loaded)
                    {
                        reusedLoader = package.Manifest.Name;
                        result = true;
                    }
                    else
                    {
                        reports.Add(CoreText.Get(
                            "Manager_AlreadyRegistered",
                            package.Manifest.Name,
                            slot.State));
                        result = false;
                    }
                }
                else if (DependencyProblem(package.Manifest) is { } dependencyProblem)
                {
                    reports.Add($"{package.Manifest.Name}: {dependencyProblem}");
                    result = false;
                }
                else
                {
                    result = LoadOne(package, enable: false, reports);
                }
            }
        }
        finally
        {
            ReportAll(reports);
        }

        // 父级的日志回调可能拿父级的锁；出了 _gate 再写，避免与卸载路径互相等。
        if (reusedLoader is not null)
        {
            _loggerFactory.CreateLogger("Hosihikari")
                .LogInformation("{Message}", CoreText.Get("Manager_LoaderReused", reusedLoader));
        }

        return result;
    }

    /// <summary>
    /// 启用一个在场的插件（ADR-0003：父级能单独启停子插件）。父级决定启停，
    /// 框架只把钩子调到并开关作用域闸门（ADR-0035）。
    /// </summary>
    public bool Enable(string name)
    {
        List<string>? reports = null;
        Slot? faulted = null;
        PluginDiagnostic? fault = null;
        bool result;
        lock (_gate)
        {
            result = DriveHook(name, enable: true, ref reports, out faulted, out fault);
        }

        ReportAll(reports);
        if (faulted is not null && fault is not null)
        {
            ReportFault(faulted.Manifest.Name, faulted.Id, fault);
        }

        return result;
    }

    /// <summary>
    /// 停用一个在场的插件：先关闭它的作用域闸门，再调 <see cref="IPlugin.OnDisable"/>；
    /// 拒绝时重开（ADR-0035）。订阅与定时器仍保留到**卸载**时才收回。
    /// </summary>
    public bool Disable(string name)
    {
        List<string>? reports = null;
        Slot? faulted = null;
        PluginDiagnostic? fault = null;
        bool result;
        lock (_gate)
        {
            result = DriveHook(name, enable: false, ref reports, out faulted, out fault);
        }

        ReportAll(reports);
        if (faulted is not null && fault is not null)
        {
            ReportFault(faulted.Manifest.Name, faulted.Id, fault);
        }

        return result;
    }

    private static string VerbText(bool enable) => CoreText.Get(enable ? "Verb_Enable" : "Verb_Disable");

    /// <summary>
    /// 把钩子投到线程池再等。装着服务端线程 <c>SynchronizationContext</c> 时，直接在服务端线程上
    /// <c>GetAwaiter().GetResult()</c> 会让钩子里的 <c>await Task.Delay</c> / <c>Task.Yield</c> 永远排不上，
    /// 调用方与钩子互相等死；线程池没有这个上下文，阻塞等就安全。
    ///
    /// 超时是第二段预算（ADR-0063）：到点取消令牌、放弃等待、记一条账，调用方照常往下走。
    /// 取消是协作式的，被放弃的钩子任务可能迟到完成；迟到的东西不再回写状态。
    /// 报告进 <paramref name="reports"/> 而不是当场回调 —— 调用方多半正握着 <see cref="_gate"/>。
    /// </summary>
    /// <returns>钩子在预算内返回为 <see langword="true"/>；超时（含钩子抛出取消）为 <see langword="false"/>。</returns>
    private bool TryInvokeHook<T>(
        string name,
        string hook,
        Func<CancellationToken, ValueTask<T>> call,
        List<string> reports,
        out T result)
        where T : struct
    {
        // 预算用 CTS 的 CancelAfter 而不是 WaitAsync(TimeSpan)：后者的定时器要等 Task 完成才摘掉，
        // 会把钩子闭包（进而把插件的 ALC）钉住一整个超时时长（ADR-0023）。
        using var budget = CreateHookBudget();
        var task = RunOffPool(() => call(budget.Token));
        try
        {
            result = task.WaitAsync(budget.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            reports.Add(CoreText.Get("Manager_HookTimeout", name, hook, _hookTimeout));
            result = default;
            return false;
        }
    }

    /// <summary>无返回值的钩子；超时语义同上。</summary>
    private bool TryInvokeHook(
        string name,
        string hook,
        Func<CancellationToken, ValueTask> call,
        List<string> reports) =>
        TryInvokeHook<bool>(
            name,
            hook,
            async token =>
            {
                await call(token).ConfigureAwait(false);
                return true;
            },
            reports,
            out _);

    /// <summary>
    /// 在专起的一根线程上跑钩子，而不是 <see cref="Task.Run(Func{Task})"/>。
    ///
    /// 两条理由，都是实测出来的：一是调用方要阻塞等，线程池一旦被别的阻塞等待占满就不会再补员，
    /// 钩子的续体排不上，<c>WaitAsync</c> 的计时器到点把它当超时；二是这些线程不该有
    /// <c>SynchronizationContext</c>，专起线程天然没有（线程池线程可能被调用方污染）。
    /// </summary>
    private static Task<T> RunOffPool<T>(Func<ValueTask<T>> call)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var pending = call();
                if (pending.IsCompletedSuccessfully)
                {
                    completion.TrySetResult(pending.Result);
                    return;
                }

                pending.AsTask().ContinueWith(
                    static (finished, state) =>
                    {
                        var target = (TaskCompletionSource<T>)state!;
                        if (finished.IsCompletedSuccessfully)
                        {
                            target.TrySetResult(finished.Result);
                        }
                        else if (finished.IsCanceled)
                        {
                            target.TrySetCanceled();
                        }
                        else
                        {
                            target.TrySetException(finished.Exception!);
                        }
                    },
                    completion,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "hosihikari-hook",
        };
        thread.Start();
        return completion.Task;
    }

    private static Task RunOffPool(Func<ValueTask> call) =>
        RunOffPool<bool>(async () =>
        {
            await call().ConfigureAwait(false);
            return true;
        });

    private CancellationTokenSource CreateHookBudget()
    {
        var budget = new CancellationTokenSource();
        if (_hookTimeout != Timeout.InfiniteTimeSpan)
        {
            budget.CancelAfter(_hookTimeout);
        }

        return budget;
    }

    private bool DriveHook(
        string name,
        bool enable,
        ref List<string>? reports,
        out Slot? faulted,
        out PluginDiagnostic? fault)
    {
        fault = null;
        faulted = null;

        // 已放下、待回收的不算在场 —— 它已经不在跑了，钩子无处可调。
        // 例外：重载时父级会对已经从表里拿掉的名字再发一次停用，那不算错（ADR-0037）。
        if (!_slots.TryGetValue(name, out var slot) || slot.State != PluginState.Loaded)
        {
            (reports ??= []).Add(CoreText.Get("Manager_NotPresent", name, VerbText(enable)));
            return !enable;
        }

        // 两个方向都幂等：已启用再启用、本轮已经停过再停用，都不再调钩子（ADR-0013）。
        // 从没启用过的插件收到停用仍会调一次 OnDisable —— 父级就是这么驱动新交付的包的。
        if (enable ? slot.Enabled : slot.Stopped)
        {
            (reports ??= []).Add(CoreText.Get(enable ? "Manager_AlreadyEnabled" : "Manager_AlreadyDisabled", name));
            return true;
        }

        // 停用先关闸门：OnDisable 执行期间与返回之后都不再开始新的投递；拒绝时恢复。
        var scope = slot.Scope!;
        var wasEnabled = slot.Enabled;
        if (!enable)
        {
            scope.Close();
        }

        try
        {
            var reportList = reports ??= [];
            if (enable)
            {
                if (!TryInvokeHook(name, "OnEnable", slot.Handle!.Plugin!.OnEnable, reportList, out var enableResult))
                {
                    // 超时与取消保持停用（ADR-0059）：没启用过，不必开闸门，账已记过。
                    return false;
                }

                if (!enableResult.Allowed)
                {
                    reportList.Add(CoreText.Get(
                        "Manager_EnableRefused",
                        name,
                        enableResult.Reason ?? CoreText.Get("Common_NoReason")));
                    return false;
                }

                slot.Enabled = true;
                slot.Stopped = false;
                try
                {
                    scope.Open();
                }
                catch (ObjectDisposedException)
                {
                    // 等待期间并发卸载已经走了：迟到完成（或完成得太晚）的启用不再开闸门。
                }

                return true;
            }

            if (!TryInvokeHook(name, "OnDisable", slot.Handle!.Plugin!.OnDisable, reportList, out var disableResult))
            {
                // 停用超时只是不再等它：闸门保持关闭，记账后按已停处理（ADR-0063）。
                slot.Enabled = false;
                slot.Stopped = true;
                return true;
            }

            if (!disableResult.Allowed)
            {
                if (wasEnabled)
                {
                    try
                    {
                        scope.Open();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }

                reportList.Add(CoreText.Get(
                    "Manager_DisableRefused",
                    name,
                    disableResult.Reason ?? CoreText.Get("Common_NoReason")));
                return false;
            }

            slot.Enabled = false;
            slot.Stopped = true;
            return true;
        }
        catch (Exception ex)
        {
            (reports ??= []).Add(CoreText.Get(
                enable ? "Manager_EnableThrew" : "Manager_DisableThrew",
                name,
                ex.Demystify()));
            faulted = slot;
            // 不带 Exception：异常对象攥着 MethodBase，会经诊断把插件的 ALC 钉住，回收验证过不去。
            // 栈已经进了报告文本（Demystify），可读性不损失。
            fault = PluginDiagnostic.Error(
                enable ? "enable-threw" : "disable-threw",
                CoreText.Get(enable ? "Manager_EnableFault" : "Manager_DisableFault"));
            return false;
        }
    }

    /// <summary>
    /// 卸载。有依赖者时**拒绝**并如实返回，不自动级联（ADR-0004）。
    ///
    /// 卸载分两段：先把实例与作用域放下，再等 ALC 被回收。第二段没通过时状态停在
    /// <see cref="PluginState.Detached"/> 并如实回报失败 —— 再问一次就是再验一次
    /// （钉住 ALC 的东西可能是暂时的，重试真的可能通过）。
    /// </summary>
    public UnloadResult Unload(string name)
    {
        List<string>? reports = null;
        var result = UnloadCore(name, expectedInstanceId: null, ref reports);
        ReportAll(reports);
        return result;
    }

    /// <summary>父级态下的依赖检查：目标已在场，且版本满足清单约束（ADR-0039）。</summary>
    private string? DependencyProblem(PluginManifest manifest)
    {
        foreach (var dependency in manifest.Dependencies)
        {
            if (dependency.Name == PluginContractInfo.ParentName)
            {
                continue;
            }

            if (!_slots.TryGetValue(dependency.Name, out var slot) || slot.State != PluginState.Loaded)
            {
                return CoreText.Get("Manager_DependencyNotLoaded", dependency.Name);
            }

            if (!DependencyVersion.Satisfies(slot.Manifest, dependency, out var problem))
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>
    /// 载体重载前的子插件回收。Loader 包按 ADR-0002 不能撤销登记，跨 reload 保留；
    /// 它们造出来的实例与路由也留着，供新 loader 直接复用。
    /// </summary>
    internal void UnloadChildren()
    {
        List<string>? reports = [];
        try
        {
            // 先快照名字与装载编号：验证在锁外进行，期间可能有同名重载。
            // 按编号回收，避免把新实例当成旧的卸掉。
            (string Name, Guid Id)[] targets;
            lock (_gate)
            {
                targets = _slots
                    .Where(entry => !entry.Value.IsLoader)
                    .Select(entry => (entry.Key, entry.Value.Id))
                    .Reverse()
                    .ToArray();
            }

            foreach (var (name, id) in targets)
            {
                var result = UnloadCore(name, id, ref reports);
                var error = result.Error;
                if (!result.Released && error is not null)
                {
                    reports?.Add($"{name}: {error}");
                }
            }
        }
        finally
        {
            ReportAll(reports);
        }
    }

    /// <summary>
    /// 卸载的加锁部分只做依赖检查与 Detach；回收验证在锁外等待（ADR-0023）。
    /// 同一槽位的并发卸载共享同一个进行中的验证；失败后清空任务，下一次重新验证。
    /// 报告先攒着，出了锁再发 —— 父级的日志回调可能拿父级自己的锁，
    /// 而父级线程可能正握着那把锁等 <see cref="_gate"/>；在锁内回调会互相等死。
    /// 钩子与回收验证各有一段预算（ADR-0063）；前者超时只在结果上标注，后者超时才是失败。
    /// </summary>
    private UnloadResult UnloadCore(string name, Guid? expectedInstanceId, ref List<string>? reports)
    {
        Slot slot;
        Task<bool> verification;
        var hookTimedOut = false;
        var hookThrew = false;
        lock (_gate)
        {
            if (!_slots.TryGetValue(name, out slot!)
                || (expectedInstanceId is { } expected && slot.Id != expected))
            {
                return UnloadResult.Failed(CoreText.Get("Manager_NotLoaded", name));
            }

            // 加载器的登记不能撤销：它的类型住在自己的 ALC 里，放掉实例会让路由指向死代码。
            if (slot.IsLoader)
            {
                return UnloadResult.Failed(CoreText.Get("Manager_LoaderPinned", name));
            }

            if (slot.State == PluginState.Loaded)
            {
                var dependents = _slots.Values
                    // 待回收的插件已经放下实例，重试时仍要依赖在场，所以也算。
                    .Where(loaded => loaded.State is PluginState.Loaded or PluginState.Detached)
                    .Where(p => p.Manifest.Dependencies.Any(d => d.Name == name))
                    .Select(p => p.Manifest.Name)
                    .ToArray();
                if (dependents.Length > 0)
                {
                    return UnloadResult.Failed(
                        CoreText.Get("Manager_HasDependents", string.Join(", ", dependents)));
                }

                var refusal = Detach(slot, ref reports, out hookTimedOut, out hookThrew);
                if (refusal is not null)
                {
                    return UnloadResult.Failed(
                        CoreText.Get("Manager_DisableRefusedByPlugin", name, refusal),
                        UnloadFailure.HookRefused);
                }

                // 钩子超时只是那一段不再等；回收验证照常进行，结局如实标注是哪一段超的。
                PublishState(name, PluginState.Loaded, PluginState.Detached);
            }

            if (slot.State != PluginState.Detached)
            {
                return UnloadResult.Failed(
                    CoreText.Get("Manager_StateAnomaly", name, slot.State));
            }

            if (slot.Verification is null)
            {
                var handle = slot.Handle;
                slot.Verification = handle is null
                    ? Task.FromResult(false)
                    : Task.Run(() => Collected(handle));
            }

            verification = slot.Verification;
        }

        bool released;
        var timedOut = false;
        try
        {
            released = _unloadVerificationTimeout == Timeout.InfiniteTimeSpan
                ? verification.GetAwaiter().GetResult()
                : verification.WaitAsync(_unloadVerificationTimeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            (reports ??= []).Add(
                CoreText.Get("Manager_UnloadTimeout", name, _unloadVerificationTimeout));
            released = false;
            timedOut = true;
        }
        catch (Exception ex)
        {
            (reports ??= []).Add(
                CoreText.Get("Manager_UnloadVerifyThrew", name, ex.Demystify()));
            released = false;
        }

        lock (_gate)
        {
            if (!_slots.TryGetValue(name, out var current) || !ReferenceEquals(current, slot))
            {
                // 已由并发的卸载成功移除；这一次也如实算成功。
                return released
                    ? UnloadResult.Ok(hookTimedOut)
                    : UnloadResult.Failed(CoreText.Get("Manager_UnloadNotRegistered", name));
            }

            // 超时保留进行中的任务，下一次卸载继续等它；其它失败清空任务，下一次重新验证。
            if (!timedOut && ReferenceEquals(current.Verification, verification))
            {
                current.Verification = null;
            }

            if (released)
            {
                _slots.Remove(name);
                return UnloadResult.Ok(hookTimedOut);
            }

            if (timedOut)
            {
                return UnloadResult.Failed(
                    CoreText.Get("Manager_UnloadTimeoutRetry", name, _unloadVerificationTimeout),
                    hookTimedOut ? UnloadFailure.HookAndReclamationTimedOut : UnloadFailure.ReclamationTimedOut);
            }

            // 说"回收"而不是"ALC"：释放句柄长什么样是 Loader 自己的事（ADR-0010）。
            return UnloadResult.Failed(
                CoreText.Get("Manager_UnloadVerifyFailedRetry", name),
                hookThrew ? UnloadFailure.HookThrew : UnloadFailure.ReclamationFailed);
        }
    }

    private void ReportAll(List<string>? reports)
    {
        if (reports is null)
        {
            return;
        }

        foreach (var report in reports)
        {
            _reportFault(report);
        }
    }

    /// <summary>诊断进操作员文本时带上错误码与严重级别：可读说明之外，机器可读的那半也得留下（ADR-0059）。</summary>
    private static string Describe(string key, string name, PluginDiagnostic diagnostic) =>
        CoreText.Get(key, name, diagnostic.Code, diagnostic.Severity, diagnostic.Message);

    /// <summary>
    /// 故障：钩子抛出，或 Loader 在钩子之外发现插件死了（ADR-0030）。
    /// 由宿主发起卸载；成功后才通知载体摘账。
    /// 故障只属于发起它的那个实例：报告在锁外发，期间同名插件可能已经重新装载，
    /// 按名字找会把健康的新实例连坐卸掉。所以这里认的是装载时发给上下文的编号。
    /// 上下文只带名字与编号 —— 带上槽位或委托会把实例经闭包钉住，ALC 就回收不了。
    /// 常在 <c>OnLoad</c> 里从钩子线程调用，所以先看 <see cref="_faultGate"/> 那本账，
    /// 不满足才去拿 <see cref="_gate"/>（它正被等钩子的调用方握着）。
    /// </summary>
    internal void ReportFault(string name, Guid instanceId, PluginDiagnostic diagnostic)
    {
        List<string>? reports = null;
        lock (_faultGate)
        {
            if (_loading.TryGetValue(instanceId, out var loadingName) && loadingName == name)
            {
                // 装载钩子还没跑完，实例尚未交给表。记下来，让 LoadOne 在回滚时一并报。
                _pendingFaults[instanceId] = diagnostic;
                return;
            }
        }

        var present = false;
        lock (_gate)
        {
            if (_slots.TryGetValue(name, out var current)
                && current.Id == instanceId
                && current.State == PluginState.Loaded)
            {
                present = true;
                (reports ??= []).Add(Describe("Manager_Fault", name, diagnostic));
            }
        }

        // 验证在 _gate 外等待；expectedInstanceId 防止期间的同名重载被误卸（A45）。
        var result = present
            ? UnloadCore(name, instanceId, ref reports)
            : UnloadResult.Failed(CoreText.Get("Manager_NotLoaded", name));

        // 报告与摘账都在锁外：它们会回到父级，而父级的锁可能正被等 _gate 的线程握着。
        ReportAll(reports);
        if (!present)
        {
            return;
        }

        if (result.Released)
        {
            _onRemoved?.Invoke(name);
        }
        else
        {
            _reportFault(CoreText.Get(
                "Manager_FaultUnloadIncomplete",
                name,
                result.Error ?? CoreText.Get("Common_Unknown")));
        }
    }
    /// <summary>
    /// 停用、卸载、放下句柄与作用域，只留下回收验证。单独一个不内联的方法，是为了让本方法返回后，
    /// 插件与它的 ALC 在栈上再无强引用 —— 回收验证的前提（ADR-0023）。
    /// 状态与字段一起改完：表里留着插件等于把 ALC 钉死。
    /// 停用被拒绝时返回原因，此时什么都不放开（ADR-0030）。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private string? Detach(Slot slot, ref List<string>? reports, out bool hookTimedOut, out bool hookThrew)
    {
        var name = slot.Manifest.Name;
        var handle = slot.Handle!;
        var plugin = handle.Plugin!;
        var scope = slot.Scope!;
        var wasEnabled = scope.Close();
        var reportList = reports ??= [];
        hookTimedOut = false;
        hookThrew = false;

        try
        {
            if (!TryInvokeHook(name, "OnDisable", plugin.OnDisable, reportList, out var disableResult))
            {
                // 超时只是不再等它：闸门保持关闭，停用按已发生记账，卸载继续（ADR-0063）。
                hookTimedOut = true;
            }
            else if (!disableResult.Allowed)
            {
                if (wasEnabled)
                {
                    scope.Open();
                }

                return disableResult.Reason ?? CoreText.Get("Common_NoReason");
            }
        }
        catch (Exception ex)
        {
            reportList.Add(CoreText.Get("Manager_DisableThrew", name, ex.Demystify()));
            hookThrew = true;
        }

        try
        {
            if (!TryInvokeHook(name, "OnUnload", plugin.OnUnload, reportList))
            {
                hookTimedOut = true;
            }
        }
        catch (Exception ex)
        {
            reportList.Add(CoreText.Get("Manager_UnloadHookThrew", name, ex.Demystify()));
            hookThrew = true;
        }

        // 作者没退订的订阅、没释放的定时器，在这里一并收回 —— 否则它们把 ALC 钉在共享服务上。
        scope.ReleaseAll();

        try
        {
            handle.Unload();
        }
        catch (Exception ex)
        {
            // 放下失败仍要继续验回收：句柄可能已经放掉一部分，状态机照实记录。
            reportList.Add(CoreText.Get("Manager_ReleaseHandleThrew", name, ex.Demystify()));
        }

        slot.Scope = null;
        slot.Enabled = false;
        slot.State = PluginState.Detached;
        return null;
    }

    /// <summary>
    /// 状态迁移事件（ADR-0062）。调用方可能正握着 <see cref="_gate"/>，而 handler 会跑在发布者线程上，
    /// 回调里再调管理器就是重入 —— 所以这里总是投到线程池发，顺序仍按调用次序。
    /// </summary>
    private void PublishState(string name, PluginState previous, PluginState current)
    {
        var notification = new PluginStateChanged(name, previous, current);
        _ = Task.Run(() => _events.Publish(notification));
    }

    /// <summary>回收验证：句柄只攥着 WeakReference，不钉住 ALC。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Collected(PluginHandle handle) =>
        handle.WaitForUnloadAsync().GetAwaiter().GetResult();

    /// <summary>
    /// 包内默认 → 包外覆盖 → 环境变量，后写覆盖先写（ADR-0018）。
    /// 三段都可选 —— 完全不带配置的插件是正常情况（ADR-0009）。
    /// 分层**必须**走配置源链：HOCON 的 <c>include</c> 在这条路径上会被静默忽略。
    /// </summary>
    private IConfiguration BuildConfiguration(string name, string packageDirectory) =>
        new ConfigurationBuilder()
            .AddHoconFile(Path.Combine(packageDirectory, $"{name}.conf"), true)
            .AddHoconFile(Path.Combine(_configRoot, $"{name}.conf"), true)
            .AddEnvironmentVariables()
            .Build();

    private bool LoadOne(PluginPackage package, bool enable, List<string> reports)
    {
        var manifest = package.Manifest;
        var scope = new PluginScope(_events, _scheduler, _registry);
        // 先建这一格，让上下文报告故障时认的是本次装载，而不是后面可能顶替它的同名实例。
        var slot = new Slot(manifest);
        lock (_faultGate)
        {
            _loading[slot.Id] = manifest.Name;
        }
        PluginHandle? handle = null;
        var loadStarted = false;
        var enableStarted = false;
        var enabled = false;
        var loaded = false;
        try
        {
            var loader = LoaderSnapshot().FirstOrDefault(candidate => candidate.CanLoad(manifest));
            if (loader is null)
            {
                reports.Add(CoreText.Get("Manager_NoLoader", manifest.Name, manifest.Loader));
                return false;
            }

            if (loader is IPluginLoaderDiagnostics diagnostics)
            {
                IReadOnlyList<PluginDiagnostic> problems;
                try
                {
                    problems = diagnostics.Validate(manifest, package.Directory);
                }
                catch (Exception ex)
                {
                    reports.Add(
                        CoreText.Get("Manager_DiagnosticsThrew", manifest.Name, ex.Demystify()));
                    return false;
                }

                if (problems is { Count: > 0 })
                {
                    foreach (var problem in problems)
                    {
                        reports.Add(
                            $"{manifest.Name}: [{problem.Code}/{problem.Severity}] {problem.Message}");
                    }

                    return false;
                }
            }

            var dataDirectory = Path.Combine(_dataRoot, manifest.Name);
            Directory.CreateDirectory(dataDirectory);
            // 普通包只给 IPluginContext；加载器包多一层 ILoaderContext（ADR-0059）。
            var isLoader = manifest.PackageKind == "loader";
            var context = new PluginContext(
                manifest.Name,
                package.Directory,
                dataDirectory,
                _loggerFactory.CreateLogger(manifest.Name),
                BuildConfiguration(manifest.Name, package.Directory),
                scope,
                _carrierServices ?? PluginContext.Empty,
                this,
                slot.Id,
                isLoader,
                reports.Add);

            handle = loader.Load(manifest, package.Directory, context.ForPlugin());
            if (handle?.Plugin is null)
            {
                reports.Add(CoreText.Get("Manager_LoadFailed", manifest.Name));
                return false;
            }

            var plugin = handle.Plugin;
            loadStarted = true;
            if (!TryInvokeHook(
                    manifest.Name,
                    "OnLoad",
                    token => plugin.OnLoad(context, token),
                    reports))
            {
                // 取消或超时都按装载回滚处理（ADR-0059），下面的 finally 收起已经跑过的部分。
                return false;
            }

            if (TakePendingFault(slot.Id) is { } loadFault)
            {
                reports.Add(Describe("Manager_Fault", manifest.Name, loadFault));
                return false;
            }

            if (enable)
            {
                enableStarted = true;
                if (!TryInvokeHook(
                        manifest.Name,
                        "OnEnable",
                        plugin.OnEnable,
                        reports,
                        out var enableResult))
                {
                    // 自持态在装载里顺带启用：超时保持停用、仍算装上（ADR-0059）。
                }
                else if (!enableResult.Allowed)
                {
                    reports.Add(CoreText.Get(
                        "Manager_EnableRefusedStaysLoaded",
                        manifest.Name,
                        enableResult.Reason ?? CoreText.Get("Common_NoReason")));
                }
                else
                {
                    scope.Open();
                    enabled = true;
                }
            }

            // 装载期间同一线程报告的故障会落在这里；实例还没交给表，直接回滚。
            if (TakePendingFault(slot.Id) is { } pendingFault)
            {
                reports.Add(Describe("Manager_Fault", manifest.Name, pendingFault));
                return false;
            }

            slot.IsLoader = isLoader || IsMarkedLoader(slot.Id);
            slot.Enabled = enabled;
            slot.Handle = handle;
            slot.Scope = scope;
            _slots[manifest.Name] = slot;
            loaded = true;
            PublishState(manifest.Name, PluginState.Detached, PluginState.Loaded);
            return true;
        }
        catch (Exception ex)
        {
            reports.Add(
                CoreText.Get("Manager_LoadThrewRolledBack", manifest.Name, ex.Demystify()));
            return false;
        }
        finally
        {
            lock (_faultGate)
            {
                _loading.Remove(slot.Id);
                _pendingFaults.Remove(slot.Id);
            }
            if (!loaded)
            {
                RemoveLoaders(slot.Id);
            }
            else
            {
                ClearLoaderMark(slot.Id);
            }
            if (!loaded)
            {
                if (enableStarted)
                {
                    CleanupHook("OnDisable", async token =>
                    {
                        await handle!.Plugin!.OnDisable(token).ConfigureAwait(false);
                    });
                }

                if (loadStarted)
                {
                    CleanupHook("OnUnload", token => handle!.Plugin!.OnUnload(token));
                }

                Cleanup(scope.ReleaseAll);
                if (handle is not null)
                {
                    Cleanup(handle.Unload);
                }
            }
        }

        void CleanupHook(string hook, Func<CancellationToken, ValueTask> cleanup)
        {
            try
            {
                TryInvokeHook(manifest.Name, hook, cleanup, reports);
            }
            catch (Exception ex)
            {
                reports.Add(
                    CoreText.Get("Manager_LoadRollbackThrew", manifest.Name, ex.Demystify()));
            }
        }

        void Cleanup(Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception ex)
            {
                reports.Add(
                    CoreText.Get("Manager_LoadRollbackThrew", manifest.Name, ex.Demystify()));
            }
        }
    }

    /// <summary>
    /// 表里的一格。两段式卸载要求它在状态切换时把指向插件与 ALC 的字段一并清掉（ADR-0023）——
    /// 所以这里放的是可空字段而不是 record。
    /// </summary>
    private sealed class Slot(PluginManifest manifest)
    {
        public PluginManifest Manifest { get; } = manifest;

        /// <summary>本次装载的编号：故障报告认它，同名重载后的新实例编号不同。</summary>
        public Guid Id { get; } = Guid.NewGuid();

        public PluginState State { get; set; } = PluginState.Loaded;

        /// <summary>在场时指向插件；放下后仍留着，用来做回收验证（它只攥一个 WeakReference）。</summary>
        public PluginHandle? Handle { get; set; }

        public PluginScope? Scope { get; set; }

        /// <summary>只在 Detached 时存在；并发卸载共享同一个进行中的验证任务。</summary>
        public Task<bool>? Verification { get; set; }

        /// <summary>Loader 包；载体重载时保留，供新 loader 直接复用。</summary>
        public bool IsLoader { get; set; }

        /// <summary>启用状态；重复 Enable/Disable 不能把闸门留在错误的一侧。</summary>
        public bool Enabled { get; set; }

        /// <summary>本轮实例已经停过；再停一次就不再调 OnDisable（ADR-0013）。</summary>
        public bool Stopped { get; set; }
    }
}
