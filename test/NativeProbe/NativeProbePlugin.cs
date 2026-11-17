using System.Runtime.InteropServices;

using Hosihikari;

[assembly: EntryPoint<NativeProbe.NativeProbePlugin>]

namespace NativeProbe;

public sealed class NativeProbePlugin : IPlugin
{
    public ValueTask OnLoad(IPluginContext context, CancellationToken cancellationToken)
    {
        var value = Probe.Value();
        File.WriteAllText(Path.Combine(context.DataDirectory, "native.txt"), value.ToString());
        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginHookResult> OnEnable(CancellationToken cancellationToken) =>
        ValueTask.FromResult(PluginHookResult.Allow());

    public ValueTask<PluginHookResult> OnDisable(CancellationToken cancellationToken) =>
        ValueTask.FromResult(PluginHookResult.Allow());

    public ValueTask OnUnload(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal static class Probe
{
    [DllImport("hosihikari_probe", EntryPoint = "hosihikari_probe")]
    public static extern int Value();
}
