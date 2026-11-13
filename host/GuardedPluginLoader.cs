using Hosihikari;

namespace Hosihikari.Spike;

/// <summary>
/// 检验拒绝与故障两条路径（ADR-0030）：拒绝是值，异常与钩子之外的死亡是故障。
/// 三个包共用这一个加载器，按名字选行为：Guarded 拒绝、Faulty 抛出、Vanishing 异步死亡。
/// </summary>
public sealed class GuardedPluginLoader : IPluginLoader
{
    /// <summary>为真时 <c>Guarded</c> 的启用返回拒绝。</summary>
    public bool RefuseEnable { get; set; }

    /// <summary>为真时 <c>Guarded</c> 的停用返回拒绝。</summary>
    public bool RefuseDisable { get; set; }

    /// <summary>为真时 <c>Faulty</c> 的启用抛出；置假可让同名重载后的实例健康启用。</summary>
    public bool ThrowOnEnable { get; set; } = true;

    public string Id => "guarded";

    public bool CanLoad(PluginManifest manifest) => manifest.Loader == Id;

    public PluginHandle? Load(PluginManifest manifest, string packageDirectory, IPluginContext context) =>
        new TestPluginHandle(new GuardedPlugin(manifest.Name, this, context));

    private sealed class GuardedPlugin(string name, GuardedPluginLoader loader, IPluginContext context) : IPlugin
    {
        public ValueTask OnLoad(IPluginContext loadedContext, CancellationToken cancellationToken)
        {
            if (name == "Vanishing")
            {
                // 模拟 Loader 在钩子之外发现的死亡：钩子返回后，从后台报告故障。
                context.Scheduler.ScheduleOnce(
                    TimeSpan.FromMilliseconds(50),
                    () => context.ReportFault(PluginDiagnostic.Error("async-death", "模拟异步死亡")));
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<PluginHookResult> OnEnable(CancellationToken cancellationToken)
        {
            if (name == "Faulty" && loader.ThrowOnEnable)
            {
                throw new InvalidOperationException("故意在启用时抛出");
            }

            return ValueTask.FromResult(loader.RefuseEnable
                ? PluginHookResult.Refuse("现在不行")
                : PluginHookResult.Allow());
        }

        public ValueTask<PluginHookResult> OnDisable(CancellationToken cancellationToken) =>
            ValueTask.FromResult(loader.RefuseDisable ? PluginHookResult.Refuse("还不想停") : PluginHookResult.Allow());

        public ValueTask OnUnload(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
