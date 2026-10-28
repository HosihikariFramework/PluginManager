# Hosihikari.Testing

Test host and in-memory host services for Hosihikari plugins. It does not start BDS, create an
assembly load context, or read a manifest; it drives the public `IPlugin` contract directly.

```csharp
using Hosihikari.Testing;

using var host = new PluginTestHost(new MyPlugin());
host.RunLifecycle();

// host.HookLog: load → enable → disable → unload
```

The host mirrors the production scope: event and timer callbacks only fire while enabled, and
`Unload` releases subscriptions, timers, and services. The context is a `TestPluginContext`:

- `TestEvents` — synchronous event bus; handler exceptions land in `HandlerErrors`.
- `TestScheduler` — manual scheduler; `RunOnce()` and `RunPeriodic()` decide when work runs.
- `TestServices` — in-memory plugin service registry, first registration wins.
- `Faults` — strings reported through `IPluginContext.ReportFault`.
- `Logger`, `Configuration`, `CarrierServices` are replaceable for a test.

For integration tests that need real discovery, dependency ordering, or unload verification, use
`PluginManagerHarness`. It creates temporary `plugins/`, `data/`, and `config/` roots and exposes the
real `PluginManager` plus captured reports:

```csharp
using var harness = new PluginManagerHarness();
harness.CreatePackage("MyPlugin", manifestJson, ("main.cs", source));
harness.Manager.RegisterLoader(new MyLoader());
harness.Load("MyPlugin");
```

The core is distributed as a release archive and as the `Hosihikari.PluginManager` NuGet package,
starting at 1.1.0. `Hosihikari.Testing` is MIT; the core package is LGPL-3.0-or-later.
