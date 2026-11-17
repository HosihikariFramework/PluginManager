using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Hosihikari.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hosihikari;

/// <summary>
/// 载体包的启动装配（ADR-0061 / 0064）：读载体清单、并共享层、跑服务配置器，交出容器与组件清单。
/// 原生入口拿到载体目录与宿主句柄后走这里；宿主断言也用它，所以是 internal 而不是私有。
/// </summary>
internal sealed class CarrierStartup
{
    private CarrierStartup(
        IServiceProvider services,
        IReadOnlyList<Assembly> sharedAssemblies,
        IReadOnlyList<ManifestCarrierComponent> components,
        string? directory)
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
        out string? error)
    {
        error = null;
        var logger = loggerFactory.CreateLogger("Hosihikari");
        var services = new ServiceCollection().AddSingleton(loggerFactory);
        var shared = new List<Assembly>();

        if (carrierDirectory is { Length: > 0 })
        {
            var manifestPath = Path.Combine(carrierDirectory, "hosihikari.json");
            if (File.Exists(manifestPath))
            {
                PluginManifest? manifest;
                try
                {
                    manifest = JsonSerializer.Deserialize<PluginManifest>(
                        File.ReadAllText(manifestPath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    error = CoreText.Get("Native_CarrierManifestInvalid", manifestPath, ex.Message);
                    return new CarrierStartup(services.BuildServiceProvider(), shared, [], carrierDirectory);
                }

                if (manifest is null || manifest.PackageKind != "carrier")
                {
                    error = CoreText.Get(
                        "Native_CarrierManifestInvalid",
                        manifestPath,
                        CoreText.Get("Native_CarrierManifestKind", manifest?.PackageKind ?? "none"));
                    return new CarrierStartup(services.BuildServiceProvider(), shared, [], carrierDirectory);
                }

                var carrier = manifest.Carrier;
                if (carrier is not null)
                {
                    foreach (var name in carrier.SharedAssemblies)
                    {
                        if (TryResolveAssembly(carrierDirectory, name, logger) is { } assembly)
                        {
                            shared.Add(assembly);
                        }
                        else
                        {
                            error = CoreText.Get("Native_CarrierSharedMissing", name);
                            return new CarrierStartup(services.BuildServiceProvider(), shared, [], carrierDirectory);
                        }
                    }

                    if (carrier.Bootstrap is { } bootstrap
                        && (bootstrap.Assembly.Length > 0 || bootstrap.Type.Length > 0))
                    {
                        if (!TryConfigure(carrierDirectory, bootstrap, services, hostHandle, logger, out var problem))
                        {
                            error = problem;
                            return new CarrierStartup(services.BuildServiceProvider(), shared, [], carrierDirectory);
                        }
                    }

                    logger.LogInformation(
                        "{Message}",
                        CoreText.Get(
                            "Native_CarrierStage",
                            carrierDirectory,
                            carrier.Components.Count,
                            carrier.Bootstrap?.Type is { Length: > 0 } type ? type : CoreText.Get("Common_None")));

                    return new CarrierStartup(
                        services.BuildServiceProvider(),
                        shared,
                        carrier.Components,
                        carrierDirectory);
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
        out string? problem)
    {
        problem = null;
        try
        {
            var assembly = TryResolveAssembly(carrierDirectory, bootstrap.Assembly, logger);
            var type = assembly?.GetType(bootstrap.Type, throwOnError: false);
            if (type is null || Activator.CreateInstance(type) is not ICarrierServicesConfigurator configurator)
            {
                problem = CoreText.Get("Native_CarrierBootstrapMissing", bootstrap.Assembly, bootstrap.Type);
                return false;
            }

            configurator.Configure(services, hostHandle);
            return true;
        }
        catch (Exception ex)
        {
            problem = CoreText.Get("Native_CarrierBootstrapFailed", bootstrap.Type, ex.Demystify());
            return false;
        }
    }

    /// <summary>先按包内文件装，再按简单名从默认上下文找（契约这类共享层就在默认上下文里）。</summary>
    private static Assembly? TryResolveAssembly(string carrierDirectory, string fileName, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(carrierDirectory, fileName);
            return File.Exists(path)
                ? Assembly.LoadFrom(path)
                : Assembly.Load(new AssemblyName(Path.GetFileNameWithoutExtension(fileName)));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            logger.LogError(ex, "{Message}", CoreText.Get("Native_CarrierAssemblyFailed", fileName));
            return null;
        }
    }
}
