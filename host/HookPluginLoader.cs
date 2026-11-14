using Hosihikari;

namespace Hosihikari.Spike;

/// <summary>
/// 钩子语义（ADR-0059 / 0063）的检验加载器，按包名选行为：
/// <c>Awaiting</c> 的四个钩子都 <c>await Task.Delay(1)</c>（挂着服务端线程上下文时，同步阻塞等会死锁）；
/// <c>Hanging</c> 的停用与卸载一直等到令牌取消才返回（验钩子超时）；<c>ContextKind</c> 记录拿到的上下文类型。
/// </summary>
public sealed class HookPluginLoader : IPluginLoader
{
    /// <summary>记录每个包拿到的上下文是不是 <see cref="ILoaderContext"/>。</summary>
    public Dictionary<string, bool> SawLoaderContext { get; } = new(StringComparer.Ordinal);

    /// <summary>各包 <c>OnLoad</c> 里 <c>Task.Delay</c> 之后是否真的跑到了。</summary>
    public Dictionary<string, bool> DelaysCompleted { get; } = new(StringComparer.Ordinal);

    public string Id => "hooking";

    public bool CanLoad(PluginManifest manifest) => manifest.Loader == Id;

    public PluginHandle? Load(PluginManifest manifest, string packageDirectory, IPluginContext context)
    {
        SawLoaderContext[manifest.Name] = context is ILoaderContext;
        return new TestPluginHandle(new HookPlugin(manifest.Name, this));
    }

    private sealed class HookPlugin(string name, HookPluginLoader loader) : IPlugin
    {
        public async ValueTask OnLoad(IPluginContext context, CancellationToken cancellationToken)
        {
            // 关键复现点：装上服务端线程上下文后，同步阻塞等会让这个续体永远排不上。
            await Task.Yield();
            await Task.Delay(1, cancellationToken);
            await Task.Run(static () => { }, cancellationToken);
            loader.DelaysCompleted[name] = true;
        }

        public async ValueTask<PluginHookResult> OnEnable(CancellationToken cancellationToken)
        {
            if (name == "Hanging")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            else
            {
                await Task.Delay(1, cancellationToken);
            }

            return PluginHookResult.Allow();
        }

        public async ValueTask<PluginHookResult> OnDisable(CancellationToken cancellationToken)
        {
            if (name == "Hanging")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            else
            {
                await Task.Delay(1, cancellationToken);
            }

            return PluginHookResult.Allow();
        }

        public async ValueTask OnUnload(CancellationToken cancellationToken)
        {
            if (name == "Hanging")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            else
            {
                await Task.Delay(1, cancellationToken);
            }
        }
    }
}
