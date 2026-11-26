using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Hosihikari.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hosihikari;

/// <summary>
/// 载体包的启动装配（FX-0061 / 0064）：读载体清单、并共享层、跑服务配置器，交出容器与组件清单。
/// 原生入口拿到载体目录与宿主句柄后走这里；宿主断言也用它，所以是 internal 而不是私有。
/// </summary>
internal sealed class CarrierStartup
{
    private static readonly object ResolverGate = new();
    private static string? s_resolvedDirectory;

    private CarrierStartup(
        IServiceProvider services,
        IReadOnlyList<Assembly> sharedAssemblies,
        IReadOnlyList<ManifestCarrierComponent> components,
        string? directory
    )
    {
        Services = services;
        SharedAssemblies = sharedAssemblies;
        Components = components;
        Directory = directory;
    }

    /// <summary>交给管理器与插件上下文的载体服务提供器。</summary>
    public IServiceProvider Services { get; }

    /// <summary>载体声明的共享层程序集（连同契约基座一起交给加载器）。</summary>
    public IReadOnlyList<Assembly> SharedAssemblies { get; }

    /// <summary>清单里声明的进程级组件；没有载体清单时为空。</summary>
    public IReadOnlyList<ManifestCarrierComponent> Components { get; }

    /// <summary>载体包目录；没有时为空。</summary>
    public string? Directory { get; }

    /// <summary>
    /// 按载体目录装配启动。目录为空或没有清单时给一个只有日志工厂的容器（验证宿主与旧路径）。
    /// </summary>
    /// <param name="carrierDirectory">垫片传来的载体包目录；没有时为空。</param>
    /// <param name="hostHandle">垫片传来的不透明宿主句柄；没有时为零。</param>
    /// <param name="loggerFactory">核心自己的日志工厂，同时注册进容器。</param>
    /// <param name="error">失败原因；成功时为空。</param>
    public static CarrierStartup Create(
        string? carrierDirectory,
        nint hostHandle,
        ILoggerFactory loggerFactory,
        out string? error
    )
    {
        error = null;
        var logger = loggerFactory.CreateLogger("Hosihikari");
        // 目录一律绝对：配置器要拿它装包内文件，相对路径会随进程工作目录漂。
        if (carrierDirectory is { Length: > 0 })
        {
            carrierDirectory = Path.GetFullPath(carrierDirectory);
        }
        var services = new ServiceCollection().AddSingleton(loggerFactory);
        var shared = new List<Assembly>();

        if (carrierDirectory is { Length: > 0 })
        {
            EnsurePackageResolver(carrierDirectory, logger);
            var manifestPath = Path.Combine(carrierDirectory, "hosihikari.json");
            if (File.Exists(manifestPath))
            {
                PluginManifest? manifest;
                try
                {
                    manifest = JsonSerializer.Deserialize<PluginManifest>(
                        File.ReadAllText(manifestPath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );
                }
                catch (Exception ex)
                    when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    error = CoreText.Get("Native_CarrierManifestInvalid", manifestPath, ex.Message);
                    return new CarrierStartup(
                        services.BuildServiceProvider(),
                        shared,
                        [],
                        carrierDirectory
                    );
                }

                if (manifest is null || manifest.PackageKind != "carrier")
                {
                    error = CoreText.Get(
                        "Native_CarrierManifestInvalid",
                        manifestPath,
                        CoreText.Get("Native_CarrierManifestKind", manifest?.PackageKind ?? "none")
                    );
                    return new CarrierStartup(
                        services.BuildServiceProvider(),
                        shared,
                        [],
                        carrierDirectory
                    );
                }

                var carrier = manifest.Carrier;
                if (carrier is not null)
                {
                    foreach (var name in carrier.SharedAssemblies)
                    {
                        if (ResolveAssembly(carrierDirectory, name, logger) is { } assembly)
                        {
                            shared.Add(assembly);
                        }
                        else
                        {
                            error = CoreText.Get("Native_CarrierSharedMissing", name);
                            return new CarrierStartup(
                                services.BuildServiceProvider(),
                                shared,
                                [],
                                carrierDirectory
                            );
                        }
                    }

                    if (
                        carrier.Bootstrap is { } bootstrap
                        && (bootstrap.Assembly.Length > 0 || bootstrap.Type.Length > 0)
                    )
                    {
                        if (
                            !TryConfigure(
                                carrierDirectory,
                                bootstrap,
                                services,
                                hostHandle,
                                logger,
                                out var problem
                            )
                        )
                        {
                            error = problem;
                            return new CarrierStartup(
                                services.BuildServiceProvider(),
                                shared,
                                [],
                                carrierDirectory
                            );
                        }
                    }

                    logger.LogInformation(
                        "{Message}",
                        CoreText.Get(
                            "Native_CarrierStage",
                            carrierDirectory,
                            carrier.Components.Count,
                            carrier.Bootstrap?.Type is { Length: > 0 } type
                                ? type
                                : CoreText.Get("Common_None"),
                            hostHandle == 0
                                ? CoreText.Get("Common_No")
                                : hostHandle.ToString("X", CultureInfo.InvariantCulture)
                        )
                    );

                    return new CarrierStartup(
                        services.BuildServiceProvider(),
                        shared,
                        carrier.Components,
                        carrierDirectory
                    );
                }
            }
        }

        return new CarrierStartup(services.BuildServiceProvider(), shared, [], carrierDirectory);
    }

    private static bool TryConfigure(
        string carrierDirectory,
        ManifestBootstrap bootstrap,
        IServiceCollection services,
        nint hostHandle,
        ILogger logger,
        out string? problem
    )
    {
        problem = null;
        try
        {
            var assembly = ResolveAssembly(carrierDirectory, bootstrap.Assembly, logger);
            var type = assembly?.GetType(bootstrap.Type, throwOnError: false);
            if (
                type is null
                || Activator.CreateInstance(type) is not ICarrierServicesConfigurator configurator
            )
            {
                problem = CoreText.Get(
                    "Native_CarrierBootstrapMissing",
                    bootstrap.Assembly,
                    bootstrap.Type
                );
                return false;
            }

            configurator.Configure(
                new CarrierServiceContext(services, carrierDirectory, hostHandle)
            );
            return true;
        }
        catch (Exception ex)
        {
            problem = CoreText.Get("Native_CarrierBootstrapFailed", bootstrap.Type, ex.Demystify());
            return false;
        }
    }

    private sealed class CarrierServiceContext(
        IServiceCollection services,
        string carrierDirectory,
        nint hostHandle
    ) : ICarrierServiceContext
    {
        public IServiceCollection Services => services;

        public string CarrierDirectory => carrierDirectory;

        public nint HostHandle => hostHandle;
    }

    /// <summary>载体包自己的依赖也从包目录解析：只在正常解析失败后才轮到它。</summary>
    /// <remarks>
    /// 按路径装程序集不会为它挂上包目录探测（那正是 <see cref="Assembly.LoadFrom(string)"/>
    /// 干的事，也是同名类型装成两份的来源）。但包里的私有依赖确实只住在包目录：
    /// 在核心所在的上下文上补一个解析器，命中包内文件才装，其余交回原路径——已装着的
    /// 契约因此仍是一份。
    /// </remarks>
    private static void EnsurePackageResolver(string carrierDirectory, ILogger logger)
    {
        var context =
            AssemblyLoadContext.GetLoadContext(typeof(CarrierStartup).Assembly)
            ?? AssemblyLoadContext.Default;

        lock (ResolverGate)
        {
            if (
                string.Equals(
                    s_resolvedDirectory,
                    carrierDirectory,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return;
            }

            context.Resolving += (loadContext, name) =>
            {
                if (string.IsNullOrEmpty(name.Name))
                {
                    return null;
                }

                var candidate = Path.Combine(carrierDirectory, name.Name + ".dll");
                if (!File.Exists(candidate))
                {
                    return null;
                }

                try
                {
                    return loadContext.LoadFromAssemblyPath(candidate);
                }
                catch (Exception ex) when (ex is FileLoadException or BadImageFormatException)
                {
                    logger.LogError(
                        ex,
                        "{Message}",
                        CoreText.Get("Native_CarrierAssemblyFailed", name.Name + ".dll")
                    );
                    return null;
                }
            };

            s_resolvedDirectory = carrierDirectory;
        }
    }

    /// <summary>先认已经装着的同身份程序集，再按包内文件装过去，最后按简单名找。</summary>
    /// <remarks>
    /// 装进**核心自己所在的那个上下文**，不用默认上下文：原生宿主经 hostfxr 的组件方式起
    /// 托管核心，核心落在 <c>IsolatedComponentLoadContext</c> 里，契约这些依赖只在那张表上；
    /// 装进默认上下文的话，bootstrap 对契约的引用解析不到（真机验收：GetType 抛
    /// FileNotFoundException）。也不用 <see cref="Assembly.LoadFrom(string)"/>：它挂的是包目录
    /// 探测，会把已在上下文里的契约**再装一份**，同名类型于是有两个类型对象，转型失败。
    /// </remarks>
    internal static Assembly? ResolveAssembly(
        string carrierDirectory,
        string fileName,
        ILogger logger
    )
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var simpleName = Path.GetFileNameWithoutExtension(fileName);
        var context =
            AssemblyLoadContext.GetLoadContext(typeof(CarrierStartup).Assembly)
            ?? AssemblyLoadContext.Default;
        try
        {
            // 同一份程序集只装一次：bootstrap 与组件都要先查共享层里已经装进来的那份。
            if (
                context.Assemblies.FirstOrDefault(loaded =>
                    string.Equals(
                        loaded.GetName().Name,
                        simpleName,
                        StringComparison.OrdinalIgnoreCase
                    )
                ) is
                { } existing
            )
            {
                return existing;
            }

            var path = Path.Combine(carrierDirectory, fileName);
            return File.Exists(path)
                ? context.LoadFromAssemblyPath(Path.GetFullPath(path))
                : Assembly.Load(new AssemblyName(simpleName));
        }
        catch (Exception ex)
            when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            logger.LogError(
                ex,
                "{Message}",
                CoreText.Get("Native_CarrierAssemblyFailed", fileName)
            );
            return null;
        }
    }
}
