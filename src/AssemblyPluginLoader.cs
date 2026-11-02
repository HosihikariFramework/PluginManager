using System.Reflection;
using Hosihikari.Internal;
using McMaster.NETCore.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hosihikari;

/// <summary>内置的程序集型加载器（ADR-0002）。每个插件一个 collectible ALC，契约走共享层（ADR-0004 / ADR-0019）。</summary>
public sealed class AssemblyPluginLoader(params Assembly[] sharedSources) : IPluginLoader
{
    // 共享层 = 契约 + 契约引用的抽象包 + 当前载体的绑定程序集（ADR-0007 / ADR-0019）。
    // 两侧的类型必须同身份，转型与实现接口才成立；插件包里也不许有这些的副本。
    private static readonly Type[] BaseSharedTypes =
    [
        typeof(IPlugin),
        typeof(IPluginContext),
        typeof(PluginManifest),
        typeof(ILogger),
        typeof(ILoggerFactory),
        typeof(IConfiguration),
    ];

    /// <summary>
    /// 打包期要剔除的共享层程序集名。与 <see cref="BaseSharedTypes"/> 同源；后两项是共享
    /// 类型的运行时依赖闭包。运行期仍按类型判身份，打包期只看得见文件名。
    /// </summary>
    public static IReadOnlyList<string> SharedAssemblyFileNames { get; } =
    [
        .. BaseSharedTypes
            .Select(type => type.Assembly.GetName().Name + ".dll")
            .Distinct(StringComparer.OrdinalIgnoreCase),
        "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
        "Microsoft.Extensions.Primitives.dll",
    ];

    private readonly Type[] _sharedTypes =
    [
        .. BaseSharedTypes,
        .. sharedSources.SelectMany(source => source.GetExportedTypes()),
    ];

    public string Id => "assembly";

    public bool CanLoad(PluginManifest manifest) => manifest.Loader == Id;

    public PluginHandle? Load(PluginManifest manifest, string packageDirectory, IPluginContext context)
    {
        var entryPath = Path.Combine(packageDirectory, manifest.EntryPath);
        if (!File.Exists(entryPath))
        {
            context.Logger.LogError(
                "{Message}",
                CoreText.Get("Assembly_EntryMissing", manifest.Name, manifest.EntryPath));
            return null;
        }

        // isUnloadable 是 ADR-0004；PreferSharedTypes + sharedTypes 是让共享层保持同一身份（ADR-0019）。
        var loader = PluginLoader.CreateFromAssemblyFile(
            entryPath,
            isUnloadable: true,
            sharedTypes: _sharedTypes,
            configure: config => config.PreferSharedTypes = true);

        // 入口由程序集上的特性声明 —— 怎么找到入口是本加载器的事（ADR-0008 / ADR-0010）。
        var assembly = loader.LoadDefaultAssembly();
        var declared = assembly.GetCustomAttributesData().Count(IsEntryPoint);
        if (declared != 1)
        {
            context.Logger.LogError(
                "{Message}",
                CoreText.Get("Assembly_EntryCount", manifest.Name, declared));
            loader.Dispose();
            return null;
        }

        var entryPoint = assembly.GetCustomAttribute<EntryPointAttributeBase>()!;
        return new AssemblyPluginHandle(entryPoint.CreateInstance(), loader);
    }

    /// <summary>在已加载的共享层里按类型认入口声明（ADR-0008）。</summary>
    private static bool IsEntryPoint(CustomAttributeData attribute) =>
        typeof(EntryPointAttributeBase).IsAssignableFrom(attribute.AttributeType);
}
