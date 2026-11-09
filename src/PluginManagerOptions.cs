namespace Hosihikari;

/// <summary>插件管理器的可选配置。现有构造函数保持原样，新配置经重载传入。</summary>
public sealed class PluginManagerOptions
{
    /// <summary>
    /// 等待回收验证的时间预算。默认 30 秒；有限值必须为正且不超过计时器上限（约 49.7 天）。
    /// <see cref="Timeout.InfiniteTimeSpan"/> 关闭计时器，一直等到加载器的验证结束；代价是坏加载器
    /// 能把载体线程永久挂住。超时按卸载失败记，插件留在待回收（ADR-0042）。
    /// </summary>
    public TimeSpan UnloadVerificationTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
