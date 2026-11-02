using System.Runtime.InteropServices;
using Hosihikari.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hosihikari;

/// <summary>
/// 原生 Shim 调进来的那一个入口（ADR-0005）—— "原生 → 托管"这条缝的托管一侧。
/// 它由 <c>get_function_pointer</c> 按类型名与方法名取到，形状就是 <c>component_entry_point_fn</c>。
/// </summary>
public static class NativeHostingEntry
{
    // 插件活得比这个方法长，所以宿主与日志工厂也必须活得比它长。
    private static PluginManager? s_host;
    private static ILoggerFactory? s_loggerFactory;

    // 父级的日志出口。原生侧在启动时把自己的日志器包成一个 C 回调递进来 ——
    // 之后核心与插件的每一行都进父级日志，而不是我们自己的控制台。
    private static IntPtr s_logCallback;
    private static IntPtr s_logUser;

    // 故障卸载成功后，通知载体把父级的账摘掉（ADR-0030）。
    // 回调与 user 成对读写；StopCore 置空时要等已经进桥的通知写完。
    private static readonly Lock s_removalGate = new();
    private static IntPtr s_removalCallback;
    private static IntPtr s_removalUser;

    /// <summary>
    /// 原生垫片与托管核心之间的入口契约版本。入口方法集合、回调布局或生命周期约定
    /// 发生不兼容变化时递增；垫片启动时先读这个值。
    /// </summary>
    public const int HostingContractVersion = 1;

    /// <summary>给原生垫片读取的契约版本入口。</summary>
    [UnmanagedCallersOnly]
    public static int GetHostingContractVersion(IntPtr arg, int argSizeInBytes) => HostingContractVersion;

    /// <summary>
    /// 父级交来日志出口：<paramref name="arg"/> 指向 <c>{ void* callback; void* user; }</c>。
    /// 两个指针都要拷出来 —— 那块内存在调用方栈上，返回就没了。
    /// </summary>
    /// <param name="arg">指向那两个指针的缓冲。</param>
    /// <param name="argSizeInBytes">缓冲的字节数。</param>
    [UnmanagedCallersOnly]
    public static int SetLogSink(IntPtr arg, int argSizeInBytes)
    {
        if (arg == IntPtr.Zero || argSizeInBytes < IntPtr.Size * 2)
        {
            return 1;
        }

        s_logCallback = Marshal.ReadIntPtr(arg);
        s_logUser = Marshal.ReadIntPtr(arg, IntPtr.Size);
        NativeForwardingLoggerProvider.SetSink(s_logCallback, s_logUser);
        return s_logCallback == IntPtr.Zero ? 1 : 0;
    }

    /// <summary>
    /// 父级交来“移除”出口：<paramref name="arg"/> 指向 <c>{ void* callback; void* user; }</c>。
    /// 托管侧在故障卸载成功后调用它，载体据此摘掉父级注册表里的那一条（ADR-0030）。
    /// </summary>
    /// <param name="arg">指向那两个指针的缓冲。</param>
    /// <param name="argSizeInBytes">缓冲的字节数。</param>
    [UnmanagedCallersOnly]
    public static int SetRemovalSink(IntPtr arg, int argSizeInBytes)
    {
        if (arg == IntPtr.Zero || argSizeInBytes < IntPtr.Size * 2)
        {
            return 1;
        }

        lock (s_removalGate)
        {
            s_removalCallback = Marshal.ReadIntPtr(arg);
            s_removalUser = Marshal.ReadIntPtr(arg, IntPtr.Size);
            return s_removalCallback == IntPtr.Zero ? 1 : 0;
        }
    }

    /// <summary>
    /// 自持态入口：插件包就在 <c>plugins/</c> 下，由我们枚举装载（ADR-0018）。
    ///
    /// **不抛异常**：在别人的进程里，托管异常越过这条边界等于把宿主一起拖下水（ADR-0009）。
    /// </summary>
    /// <param name="arg">扩展位，当前未用。</param>
    /// <param name="argSizeInBytes">上面那块的大小，当前未用。</param>
    [UnmanagedCallersOnly]
    public static int Start(IntPtr arg, int argSizeInBytes) => StartCore(scanPlugins: true);

    /// <summary>
    /// 父级态入口：插件集归父级注册表所有（ADR-0003），我们等父级把插件交过来。
    /// 枚举 <c>plugins/</c> 会把父级自己的模组一并收编，所以这里不扫目录。
    /// </summary>
    /// <param name="arg">扩展位，当前未用。</param>
    /// <param name="argSizeInBytes">上面那块的大小，当前未用。</param>
    [UnmanagedCallersOnly]
    public static int StartUnderParent(IntPtr arg, int argSizeInBytes) => StartCore(scanPlugins: false);

    /// <summary>
    /// 父级要重载自己（Endstone 的 <c>/reload</c>）：把子插件全部收回，保留核心与 Loader。
    /// CLR 与 Shim 模块继续活着，下一次 <c>StartUnderParent</c> 复用已有 host。
    /// </summary>
    [UnmanagedCallersOnly]
    public static int StopUnderParent(IntPtr arg, int argSizeInBytes) => StopCore();

    private static int StopCore()
    {
        var logger = s_loggerFactory?.CreateLogger("Hosihikari");
        try
        {
            var host = s_host;
            if (host is null)
            {
                return 0;
            }

            host.UnloadChildren();
            for (var i = 0; i < 10; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            // 给暂时钉住 ALC 的子插件一次重新验证的机会；Loader 槽位不动。
            host.UnloadChildren();
            return 0;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "重载前回收子插件失败");
            return 1;
        }
        finally
        {
            // 原生模块马上会被 Endstone 卸载。新模块的 onLoad 会重新 SetLogSink；
            // 在这段空窗里让所有旧 logger 只做空操作。
            NativeForwardingLoggerProvider.SetSink(IntPtr.Zero, IntPtr.Zero);
            lock (s_removalGate)
            {
                s_removalCallback = IntPtr.Zero;
                s_removalUser = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// 父级交付一个插件包：参数是包目录的、以 null 结尾的 UTF-8 指针（ADR-0003）。
    /// 返回 0 表示装上了，非 0 表示没装上 —— 原因已经进了父级的日志。
    /// </summary>
    /// <param name="arg">包目录的 UTF-8 指针。</param>
    /// <param name="argSizeInBytes">那块缓冲的字节数，含结尾的 null。</param>
    [UnmanagedCallersOnly]
    public static int LoadPlugin(IntPtr arg, int argSizeInBytes) => Deliver(arg, Verb.Load);

    /// <summary>
    /// 父级收回一个插件：参数是插件名的、以 null 结尾的 UTF-8 指针（ADR-0003）。
    /// 返回 0 表示回收验证过了；非 0 表示放下了但还没回收，父级可以再问一次。
    /// </summary>
    /// <param name="arg">插件名的 UTF-8 指针。</param>
    /// <param name="argSizeInBytes">那块缓冲的字节数，含结尾的 null。</param>
    [UnmanagedCallersOnly]
    public static int UnloadPlugin(IntPtr arg, int argSizeInBytes) => Deliver(arg, Verb.Unload);

    /// <summary>父级单独启停一个子插件（ADR-0003）：参数是插件名的 UTF-8 指针。</summary>
    /// <param name="arg">插件名的 UTF-8 指针。</param>
    /// <param name="argSizeInBytes">那块缓冲的字节数，含结尾的 null。</param>
    [UnmanagedCallersOnly]
    public static int EnablePlugin(IntPtr arg, int argSizeInBytes) => Deliver(arg, Verb.Enable);

    /// <inheritdoc cref="EnablePlugin"/>
    [UnmanagedCallersOnly]
    public static int DisablePlugin(IntPtr arg, int argSizeInBytes) => Deliver(arg, Verb.Disable);

    /// <summary>父级能让我们做的四件事。它驱动的是同一个入口，只是动作不同。</summary>
    private enum Verb
    {
        Load,
        Unload,
        Enable,
        Disable,
    }

    private static int Deliver(IntPtr arg, Verb verb)
    {
        var logger = s_loggerFactory?.CreateLogger("Hosihikari");
        var action = ActionName(verb);

        // 整段包在 try 里：这是托管与父级之间唯一的一道缝，异常越过它等于把服务端一起拖下水
        // （ADR-0009）。一次交付失败只是这个插件没动成，父级照常跑。
        try
        {
            // UTF-8 而不是 UTF-16：原生侧的名字与路径本来就是 UTF-8，跨平台也是同一个字节形状。
            var text = arg == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(arg);
            if (string.IsNullOrEmpty(text))
            {
                logger?.LogError("父级交来的参数是空的");
                return 1;
            }

            if (s_host is null)
            {
                logger?.LogError("核心还没起来，{Action} {Name} 落空", action, text);
                return 1;
            }

            // 前三个的失败原因由核心自己报过（它会走 _reportFault）。
            switch (verb)
            {
                case Verb.Load:
                    return s_host.Load(text) ? 0 : 1;
                case Verb.Enable:
                    return s_host.Enable(text) ? 0 : 1;
                case Verb.Disable:
                    return s_host.Disable(text) ? 0 : 1;
                default:
                    var result = s_host.Unload(text);
                    if (!result.Released)
                    {
                        logger?.LogError("卸载 {Name} 没完成：{Reason}", text, result.Error);
                    }

                    return result.Released ? 0 : 1;
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "{Action} 时抛出，本次交付作废", action);
            return 1;
        }
    }

    private static string ActionName(Verb verb) => verb switch
    {
        Verb.Load => "装载",
        Verb.Unload => "卸载",
        Verb.Enable => "启用",
        _ => "停用",
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RemovalCallback(IntPtr user, IntPtr nameUtf8);

    /// <summary>把“已移除”转给载体；没有出口时（验证宿主、自持态）只留在日志里。</summary>
    private static void ForwardRemoval(string name)
    {
        // 调用与 StopCore 的置空互斥：返回后原生模块才可能卸载。
        lock (s_removalGate)
        {
            var callback = s_removalCallback;
            if (callback == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var sink = Marshal.GetDelegateForFunctionPointer<RemovalCallback>(callback);
                var text = Marshal.StringToCoTaskMemUTF8(name);
                try
                {
                    sink(s_removalUser, text);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(text);
                }
            }
            catch (Exception ex)
            {
                s_loggerFactory?.CreateLogger("Hosihikari")
                    .LogError(ex, "通知载体移除 {Name} 失败", name);
            }
        }
    }

    private static int StartCore(bool scanPlugins)
    {
        if (s_host is not null)
        {
            // Endstone reload：Loader 与旧 host 留着，靠原生回调槽换到新模块。
            s_loggerFactory?.CreateLogger("Hosihikari")
                .LogInformation("复用已有核心：Loader 保持，子插件由新 loader 重新交付");
            return 0;
        }

        // 有父级的出口就转发过去；没有（自持态、验证宿主）才落到控制台。
        s_loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            if (s_logCallback == IntPtr.Zero)
            {
                builder.AddSimpleConsole(options => options.SingleLine = true);
            }
            else
            {
                builder.AddProvider(new NativeForwardingLoggerProvider());
            }
        });
        var logger = s_loggerFactory.CreateLogger("Hosihikari");
        logger.LogInformation(
            "核心 {CoreVersion}；入口契约 {ContractVersion}；插件 API {ApiVersion}",
            typeof(PluginManager).Assembly.GetName().Version?.ToString(3) ?? "未知",
            HostingContractVersion,
            PluginContractInfo.CurrentApiVersion);

        try
        {
            // 服务端根就是进程的工作目录；配置与数据都相对它找（ADR-0018）。
            var root = Directory.GetCurrentDirectory();
            // 宿主从 services 里取 ILoggerFactory；不传的话插件拿到的是 NullLogger ——
            // 它们的每一行都会消失，而且不会有人报错。
            var services = new ServiceCollection()
                .AddSingleton(s_loggerFactory!)
                .BuildServiceProvider();

            s_host = new PluginManager(
                Path.Combine(root, "plugins"),
                Path.Combine(root, "data", "hosihikari"),
                Path.Combine(root, "config", "hosihikari"),
                entry => logger.LogError("加载问题：{Entry}", entry),
                services,
                ForwardRemoval);
            s_host.RegisterLoader(new AssemblyPluginLoader());

            if (scanPlugins)
            {
                s_host.LoadAll();
                logger.LogInformation(
                    "已加载 {Count} 个插件：[{Names}]",
                    s_host.LoadedNames.Count,
                    string.Join(", ", s_host.LoadedNames));
            }
            else
            {
                logger.LogInformation("已就绪，等待父级交付插件");
            }

            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动失败");
            return 1;
        }
    }
}
