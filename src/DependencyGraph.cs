namespace Hosihikari.Internal;

/// <summary>拓扑序加载、反序卸载、不自动级联（ADR-0004）。</summary>
internal static class DependencyGraph
{
    /// <summary>返回加载顺序；缺失依赖或成环的包被剔出来单独报告，不牵连其余包（ADR-0009）。</summary>
    internal static List<PluginPackage> Resolve(IReadOnlyList<PluginPackage> packages, Action<string> report)
    {
        var byName = packages.ToDictionary(p => p.Manifest.Name, StringComparer.Ordinal);
        var order = new List<PluginPackage>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 未访问，1 访问中，2 已完成

        foreach (var package in packages)
        {
            if (!Visit(package, byName, state, order, report))
            {
                report(CoreText.Get("Graph_Unresolved", package.Manifest.Name));
            }
        }

        return order;
    }

    private static bool Visit(
        PluginPackage package,
        Dictionary<string, PluginPackage> byName,
        Dictionary<string, int> state,
        List<PluginPackage> order,
        Action<string> report)
    {
        var name = package.Manifest.Name;
        if (state.TryGetValue(name, out var mark))
        {
            if (mark == 2)
            {
                return true;
            }

            report(CoreText.Get("Graph_Cycle", name));
            return false;
        }

        state[name] = 1;
        foreach (var dependency in package.Manifest.Dependencies)
        {
            // 对框架自身的依赖在哪个拓扑下都成立 —— 我们就是正在跑的那个（ADR-0006）。
            // 父级态下这条依赖是给父级的计划用的，自持态下没有这个包，但它同样已经在了。
            if (dependency.Name == PluginContractInfo.ParentName)
            {
                continue;
            }

            if (!byName.TryGetValue(dependency.Name, out var target))
            {
                report(CoreText.Get("Graph_MissingDependency", name, dependency.Name));
                return false;
            }

            if (!DependencyVersion.Satisfies(target.Manifest, dependency, out var versionProblem))
            {
                report($"{name}: {versionProblem}");
                return false;
            }

            if (!Visit(target, byName, state, order, report))
            {
                return false;
            }
        }

        state[name] = 2;
        order.Add(package);
        return true;
    }
}
