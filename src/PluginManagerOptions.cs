namespace Hosihikari;

/// <summary>插件管理器的可选配置。现有构造函数保持原样，新配置经重载传入。</summary>
public sealed class PluginManagerOptions
{
    /// <summary>
    /// 等一个生命周期钩子跑完的时间预算。默认 30 秒；有限值必须为正且不超过计时器上限（约 49.7 天）。
    /// <see cref="Timeout.InfiniteTimeSpan"/> 关掉计时器，一直等钩子返回；代价是挂住的插件能永久占住调用方。
    /// 超时按失败记账并继续后续阶段（ADR-0063）：<c>OnLoad</c> 按装载回滚；<c>OnEnable</c> 保持停用；
    /// <c>OnDisable</c> / <c>OnUnload</c> 只是不再等待。取消是协作式的，插件应尊重令牌。
    /// </summary>
    public TimeSpan HookTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 等待回收验证的时间预算。默认 30 秒；有限值必须为正且不超过计时器上限（约 49.7 天）。
    /// <see cref="Timeout.InfiniteTimeSpan"/> 关闭计时器，一直等到加载器的验证结束；代价是坏加载器
    /// 能把载体线程永久挂住。超时按卸载失败记，插件留在待回收（ADR-0042）。
    /// </summary>
    public TimeSpan UnloadVerificationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 等一个载体组件 <c>StopAsync</c> 收尾的时间预算。默认 30 秒；有限值必须为正且不超过计时器上限。
    /// <see cref="Timeout.InfiniteTimeSpan"/> 关掉计时器，一直等它返回。
    /// 超时后框架不再等待、记一条账，继续进程退出（ADR-0063）—— 组件可能留下未撤下的原生监听器。
    /// 与上两项互不相干：组件是进程级收尾，不该被某个插件的慢钩子混进同一个数。
    /// </summary>
    public TimeSpan ComponentStopTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
