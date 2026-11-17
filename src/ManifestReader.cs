using System.Globalization;
using System.Text.Json;

namespace Hosihikari.Internal;

/// <summary>读插件包目录里的 <c>hosihikari.json</c>（ADR-0058）。</summary>
internal static class ManifestReader
{
    private const string FileName = "hosihikari.json";

    /// <summary>1.x 的清单。只剩它、且路由给我们的包按旧包拒载。</summary>
    private const string LegacyFileName = "manifest.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>自持态用：扫 <c>plugins/</c> 下的每个目录。</summary>
    internal static List<PluginPackage> Scan(string pluginsRoot, Action<string> report)
    {
        var packages = new List<PluginPackage>();

        // 目录不存在属于正常情况，不是错误（ADR-0009）。
        if (!Directory.Exists(pluginsRoot))
        {
            return packages;
        }

        foreach (var directory in Directory.EnumerateDirectories(pluginsRoot))
        {
            // 父级自己的插件就住在同一个 plugins/ 下（ADR-0003）：既没有我们的清单、
            // 旧信封的 type 也不是 hosihikari 的目录，不吭声。
            if (File.Exists(Path.Combine(directory, FileName)))
            {
                if (Read(directory, report) is { } package
                    // 载体包就是垫片自己，不是插件（ADR-0006）。
                    && package.Manifest.PackageKind != "carrier")
                {
                    packages.Add(package);
                }
            }
            else if (IsRoutedToUs(directory))
            {
                report(CoreText.Get("Manifest_LegacyPackage", Path.GetFileName(directory)));
            }
        }

        return packages;
    }

    /// <summary>父级态用：父级逐个交付，清单只有一个读者（ADR-0003）。目录读不出来时返回 null。</summary>
    internal static PluginPackage? Read(string directory, Action<string> report)
    {
        var folderName = Path.GetFileName(directory);
        var manifestPath = Path.Combine(directory, FileName);
        if (!File.Exists(manifestPath))
        {
            // 父级只把路由给我们的包交过来，所以这里的旧信封一定是旧包（ADR-0058）。
            report(File.Exists(Path.Combine(directory, LegacyFileName))
                ? CoreText.Get("Manifest_LegacyPackage", folderName)
                : CoreText.Get("Manifest_MissingFile", folderName, FileName));
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), Options);
            if (manifest is null)
            {
                report(CoreText.Get("Manifest_Empty", folderName));
                return null;
            }

            // LeviLamina 也要求这两条，我们跟着校验，好让三个拓扑同一个包同一种失败（ADR-0006）。
            if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name != folderName)
            {
                report(CoreText.Get("Manifest_NameMismatch", folderName));
                return null;
            }

            if (string.IsNullOrWhiteSpace(manifest.EntryPath))
            {
                report(CoreText.Get("Manifest_MissingEntry", folderName));
                return null;
            }

            // ADR-0040 硬切旧字段：写旧键的包在载入前就给出改名提示，别让它落到加载器选型或版本比较上。
            if (LegacyExtraInfoKeys(manifest) is { } legacy)
            {
                report(CoreText.Get("Manifest_LegacyField", folderName, legacy.Old, legacy.New));
                return null;
            }

            // entry 是包内相对路径（ADR-0006）。绝对路径或 .. 会把包外的文件装进插件的 ALC，
            // "一个目录就是一个包"随之不成立。
            if (!IsEntryContained(directory, manifest.EntryPath))
            {
                report(CoreText.Get("Manifest_EntryOutside", folderName, manifest.EntryPath));
                return null;
            }

            // type 在 hosihikari.json 里可省；写了就必须对。它是父级的路由键，写错的包
            // 不会被父级交过来，所以在自持态下也一并拦下（ADR-0012）。
            if (!string.IsNullOrWhiteSpace(manifest.ParentName)
                && manifest.ParentName != PluginContractInfo.ParentName)
            {
                report(CoreText.Get(
                    "Manifest_ParentMismatch",
                    folderName,
                    PluginContractInfo.ParentName,
                    manifest.ParentName));
                return null;
            }

            if (manifest.EffectiveApiVersion is not { } apiVersion
                || apiVersion.ToString(CultureInfo.InvariantCulture) != PluginContractInfo.CurrentApiVersion)
            {
                report(CoreText.Get(
                    "Manifest_ApiMismatch",
                    folderName,
                    manifest.EffectiveApiVersion?.ToString(CultureInfo.InvariantCulture)
                        ?? CoreText.Get("Common_Undeclared"),
                    PluginContractInfo.CurrentApiVersion));
                return null;
            }

            // 清单 schema 版本（ADR-0012）：解析器只认这一个版本。
            if (manifest.EffectiveSchemaVersion is not { } schemaVersion
                || schemaVersion.ToString(CultureInfo.InvariantCulture) != PluginContractInfo.CurrentManifestVersion)
            {
                report(CoreText.Get(
                    "Manifest_ManifestVersionMismatch",
                    folderName,
                    manifest.EffectiveSchemaVersion?.ToString(CultureInfo.InvariantCulture)
                        ?? CoreText.Get("Common_Undeclared"),
                    PluginContractInfo.CurrentManifestVersion));
                return null;
            }

            // JSON null 会绕过集合初始化；ManifestDependency 也可能被写成 null。
            // 这里统一拒绝，别让依赖图在后面抛 NRE。
            if (manifest.Dependencies.Any(dependency =>
                    dependency is null || string.IsNullOrWhiteSpace(dependency.Name)))
            {
                report(CoreText.Get("Manifest_DependencyName", folderName));
                return null;
            }

            // 父级态不跑依赖图；自依赖会让卸载永远看到“还有依赖者”。
            if (manifest.Dependencies.Any(dependency =>
                    string.Equals(dependency.Name, manifest.Name, StringComparison.Ordinal)))
            {
                report(CoreText.Get("Manifest_SelfDependency", folderName));
                return null;
            }

            // 包类型（ADR-0006）：未知取值说明作者写错了键值，早时报出来。
            var kind = manifest.PackageKind;
            if (kind is not ("plugin" or "loader" or "carrier"))
            {
                report(CoreText.Get("Manifest_UnknownPackageKind", folderName, kind));
                return null;
            }

            if (kind == "carrier" && manifest.Name != PluginContractInfo.ParentName)
            {
                report(CoreText.Get("Manifest_CarrierNotNamed", folderName));
                return null;
            }

            // 对框架的依赖（ADR-0006）：缺了父级会把它排到垫片前面，判成 Unknown mod type。
            // 载体包就是垫片自己，不依赖自己。
            if (kind != "carrier"
                && !manifest.Dependencies.Any(dependency =>
                    string.Equals(dependency.Name, PluginContractInfo.ParentName, StringComparison.Ordinal)))
            {
                report(CoreText.Get("Manifest_MissingFrameworkDependency", folderName));
                return null;
            }

            if (!DependencyVersion.TryValidateManifest(manifest, out var versionProblem))
            {
                report(versionProblem!);
                return null;
            }

            return new PluginPackage(manifest, directory);
        }
        catch (JsonException ex)
        {
            report(CoreText.Get("Manifest_InvalidJson", folderName, ex.Message));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report(CoreText.Get("Manifest_ReadFailed", folderName, ex.Message));
            return null;
        }
    }

    /// <summary>旧信封是否由父级路由给我们 —— 决定 <c>plugins/</c> 下要不要报旧包。</summary>
    private static bool IsRoutedToUs(string directory)
    {
        var legacyPath = Path.Combine(directory, LegacyFileName);
        if (!File.Exists(legacyPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(legacyPath));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == PluginContractInfo.ParentName;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>ADR-0040 改过名的 extraInfo 键；旧键不再读取，只给一条明确的迁移提示。</summary>
    internal static (string Old, string New)? LegacyExtraInfoKeys(PluginManifest manifest)
    {
        (string Old, string New)[] renamed =
        [
            ("api", "api_version"),
            ("provider", "loader"),
            ("format_version", "manifest_version"),
        ];

        foreach (var (oldKey, newKey) in renamed)
        {
            if (manifest.ExtraInfo.ContainsKey(oldKey))
            {
                return (oldKey, newKey);
            }
        }

        return null;
    }

    /// <summary>entry 必须是包内相对路径：绝对路径与 .. 都出界。</summary>
    internal static bool IsEntryContained(string packageDirectory, string entry)
    {
        try
        {
            if (Path.IsPathRooted(entry))
            {
                return false;
            }

            var root = Path.GetFullPath(packageDirectory);
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, entry));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return full.StartsWith(prefix, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

internal sealed record PluginPackage(PluginManifest Manifest, string Directory);
