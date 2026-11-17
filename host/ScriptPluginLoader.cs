using Hosihikari;

namespace Hosihikari.Spike;

/// <summary>
/// ADR-0002 的检验：一个**完全不碰 ALC** 的插件类型，用的还是同一套 <see cref="IPluginLoader"/>。
/// 真实的脚本加载器归 ScriptManager 仓，这里只为证明核心对类型是真中立的。
/// </summary>
public sealed class ScriptPluginLoader : IPluginLoader
{
    public string Id => "script";

    public bool CanLoad(PluginManifest manifest) => manifest.Loader == Id;

    public PluginHandle? Load(PluginManifest manifest, string packageDirectory, IPluginContext context)
    {
        var scriptPath = Path.Combine(packageDirectory, manifest.EntryPath);
        if (!File.Exists(scriptPath))
        {
            return null;
        }

        return new TestPluginHandle(new ScriptPlugin(File.ReadAllText(scriptPath), context.DataDirectory));
    }
}

internal sealed class ScriptPlugin(string source, string dataDirectory) : IPlugin
{
    public ValueTask OnLoad(IPluginContext context, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[Lua] OnLoad   {source.Trim().Length} 字符的脚本");
        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginHookResult> OnEnable(CancellationToken cancellationToken)
    {
        Console.WriteLine("[Lua] OnEnable");
        File.WriteAllText(Path.Combine(dataDirectory, "ran.txt"), source.Trim());
        return ValueTask.FromResult(PluginHookResult.Allow());
    }

    public ValueTask<PluginHookResult> OnDisable(CancellationToken cancellationToken)
    {
        Console.WriteLine("[Lua] OnDisable");
        return ValueTask.FromResult(PluginHookResult.Allow());
    }

    public ValueTask OnUnload(CancellationToken cancellationToken)
    {
        Console.WriteLine("[Lua] OnUnload");
        return ValueTask.CompletedTask;
    }
}
