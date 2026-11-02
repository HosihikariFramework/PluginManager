using Hosihikari;
using NuGet.Versioning;

namespace Hosihikari.Internal;

/// <summary>清单版本与依赖约束的解析（ADR-0039）。</summary>
internal static class DependencyVersion
{
    /// <summary>校验清单里的版本与每条依赖约束的语法；缺失版本按“任意版本”处理。</summary>
    internal static bool TryValidateManifest(PluginManifest manifest, out string? problem)
    {
        problem = null;

        if (!string.IsNullOrWhiteSpace(manifest.Version)
            && !NuGetVersion.TryParse(manifest.Version, out _))
        {
            problem = CoreText.Get("Version_Invalid", manifest.Name, manifest.Version);
            return false;
        }

        foreach (var dependency in manifest.Dependencies)
        {
            if (dependency is null || string.IsNullOrWhiteSpace(dependency.Name))
            {
                problem = CoreText.Get("Manifest_DependencyName", manifest.Name);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(dependency.VersionRange)
                && !VersionRange.TryParse(dependency.VersionRange, out _))
            {
                problem = CoreText.Get(
                    "Version_InvalidRange",
                    manifest.Name,
                    dependency.Name,
                    dependency.VersionRange);
                return false;
            }
        }

        return true;
    }

    /// <summary>目标插件的版本是否满足这条依赖约束；没有约束时接受任意版本。</summary>
    internal static bool Satisfies(PluginManifest target, ManifestDependency dependency, out string? problem)
    {
        problem = null;

        if (string.IsNullOrWhiteSpace(dependency.VersionRange) || dependency.VersionRange.Trim() == "*")
        {
            return true;
        }

        if (!VersionRange.TryParse(dependency.VersionRange, out var range))
        {
            problem = CoreText.Get("Version_ConstraintInvalid", dependency.Name, dependency.VersionRange);
            return false;
        }

        if (string.IsNullOrWhiteSpace(target.Version)
            || !NuGetVersion.TryParse(target.Version, out var version))
        {
            problem = CoreText.Get("Version_NoVersion", dependency.Name, dependency.VersionRange);
            return false;
        }

        // 浮动版本（1.*、1.2.*）在 VersionRange 里没有上界，Satisfies 会放过 2.0.0；
        // 上界由 FloatRange 给出，两个都要满足。
        if (!range.Satisfies(version)
            || (range.Float is { } floatRange && !floatRange.Satisfies(version)))
        {
            problem = CoreText.Get("Version_NotSatisfied", dependency.Name, version, dependency.VersionRange);
            return false;
        }

        return true;
    }
}
