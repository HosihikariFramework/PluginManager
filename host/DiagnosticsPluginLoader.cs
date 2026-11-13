using Hosihikari;

namespace Hosihikari.Spike;

/// <summary>只负责证明诊断接缝进入加载报告；真正装载会抛异常。</summary>
public sealed class DiagnosticsPluginLoader : IPluginLoader, IPluginLoaderDiagnostics
{
    public string Id => "diagnostic";

    public bool CanLoad(PluginManifest manifest) => manifest.Loader == Id;

    public PluginHandle? Load(PluginManifest manifest, string packageDirectory, IPluginContext context) =>
        throw new InvalidOperationException("诊断没有拦下");

    public IReadOnlyList<PluginDiagnostic> Validate(PluginManifest manifest, string packageDirectory) =>
        [PluginDiagnostic.Error("entry-missing", "缺少入口文件 Diagnostic.dll")];
}
