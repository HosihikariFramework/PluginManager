using Hosihikari;

namespace Hosihikari.Spike;

/// <summary>
/// 一个在装载时就抛的加载器 —— 钉住 FX-0009 的那条边界：在别人的进程里没有调试器，
/// 日志是唯一的可见通道，所以报出去的必须是**完整异常**而不是一句概括。
///
/// 这条在真机上踩到过：那次是 McMaster 拒绝相对路径。异常被兜住、服务端照常跑，
/// 而定位全靠日志里那份堆栈。
/// </summary>
public sealed class ThrowingPluginLoader : IPluginLoader
{
    /// <summary>断言拿它当"报告里是否真的带了那条异常"的凭据。</summary>
    public const string ThrowMessage = "故意在装载时抛出";

    public string Id => "throwing";

    public bool CanLoad(PluginManifest manifest) => manifest.Loader == Id;

    public PluginHandle? Load(
        PluginManifest manifest,
        string packageDirectory,
        IPluginContext context
    ) => throw new InvalidOperationException(ThrowMessage);
}
