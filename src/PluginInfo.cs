namespace Hosihikari;

/// <summary>
/// 管理器的只读插件状态快照。它不交出插件句柄，也不改变状态（ADR-0041）。
/// </summary>
/// <param name="Name">插件名，等于包目录名。</param>
/// <param name="Version">插件版本；清单没写时为 <see langword="null"/>。</param>
/// <param name="Loader">处理这个包的加载器 id。</param>
/// <param name="State">在场状态；待回收的插件仍在表中。</param>
/// <param name="Enabled">逻辑上是否已启用。</param>
/// <param name="IsLoader">这个包是否是已登记的加载器包。</param>
/// <param name="Dependencies">清单里的依赖快照。</param>
public sealed record PluginInfo(
    string Name,
    string? Version,
    string Loader,
    PluginState State,
    bool Enabled,
    bool IsLoader,
    IReadOnlyList<PluginDependencyInfo> Dependencies);

/// <summary>一条依赖的只读快照。</summary>
/// <param name="Name">被依赖的插件名。</param>
/// <param name="VersionRange">版本约束；任意版本时为 <see langword="null"/>。</param>
public sealed record PluginDependencyInfo(string Name, string? VersionRange);
