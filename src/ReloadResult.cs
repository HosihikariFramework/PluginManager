namespace Hosihikari;

/// <summary>
/// 一次 <see cref="PluginManager.Reload"/> 的结果（ADR-0062）。三步组合里任何一步失败就停在那里，
/// 失败原因写进 <paramref name="Error"/>，不假装成功。
/// </summary>
/// <param name="Reloaded">插件是否已重新装载并启用。</param>
/// <param name="NotSupported">当前拓扑不支持重载：父级在场时插件的启停归父级驱动（ADR-0003）。</param>
/// <param name="Error">失败原因；成功与不支持时为 <see langword="null"/>。</param>
public readonly record struct ReloadResult(bool Reloaded, bool NotSupported, string? Error)
{
    /// <summary>重载完成。</summary>
    public static ReloadResult Ok() => new(true, false, null);

    /// <summary>失败：停在哪一步、为什么。</summary>
    public static ReloadResult Failed(string error) => new(false, false, error);

    /// <summary>当前拓扑不提供重载。</summary>
    public static ReloadResult Unsupported(string reason) => new(false, true, reason);
}
