using Hosihikari;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hosihikari.Testing;

/// <summary>内存版 <see cref="IPluginContext"/>：日志、配置、数据目录与宿主服务都可直接检查或替换。</summary>
public sealed class TestPluginContext : IPluginContext
{
    private readonly Dictionary<string, IPluginLoader> _loaders = new(StringComparer.Ordinal);
    private readonly List<string> _faults = [];

    public TestPluginContext(string name, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (Path.IsPathRooted(name)
            || name is "." or ".."
            || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException("Test plugin name must be a single directory name", nameof(name));
        }

        Name = name;
        PackageDirectory = Path.Combine(root, "plugins", name);
        DataDirectory = Path.Combine(root, "data", name);
        Directory.CreateDirectory(PackageDirectory);
        Directory.CreateDirectory(DataDirectory);

        Configuration = new ConfigurationBuilder().Build();
        TestEvents = new TestEventBus();
        TestScheduler = new TestScheduler();
        TestServices = new TestServiceRegistry();
        Scope = new TestPluginScope(TestEvents, TestScheduler, TestServices);
    }

    public string Name { get; }
    public string PackageDirectory { get; }
    public string DataDirectory { get; }

    public ILogger Logger { get; set; } = NullLogger.Instance;
    public IConfiguration Configuration { get; set; }

    public TestEventBus TestEvents { get; }
    public IEventBus Events => Scope;

    public TestScheduler TestScheduler { get; }
    public IScheduler Scheduler => Scope;

    public TestServiceRegistry TestServices { get; }
    public IPluginServiceRegistry PluginServices => Scope;

    internal TestPluginScope Scope { get; }

    public IServiceProvider CarrierServices { get; set; } = EmptyServices.Instance;

    public IReadOnlyList<string> Faults => _faults;

    public bool RegisterLoader(IPluginLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        return _loaders.TryAdd(loader.Id, loader);
    }

    public void ReportFault(string reason) => _faults.Add(reason);

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
