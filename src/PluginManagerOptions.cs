namespace Hosihikari;

/// <summary>插件管理器的可选配置。现有构造函数保持原样，新配置经重载传入。</summary>
public sealed class PluginManagerOptions
{
    /// <summary>
    /// 等待回收验证的时间预算。默认 30 秒；必须是正数且不超过计时器上限（约 49.7 天），
    /// 不设无限值 —— 坏加载器会永久挂住载体线程（ADR-0042）。超时按卸载失败记，插件留在待回收。
    /// </summary>
    public TimeSpan UnloadVerificationTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
