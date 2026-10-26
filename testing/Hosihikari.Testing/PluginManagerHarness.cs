using Hosihikari;

namespace Hosihikari.Testing;

/// <summary>
/// 临时目录 + 真实 <see cref="PluginManager"/> 的集成测试宿主。它负责准备 plugins/data/config
/// 三个根，并把管理器报告与“已移除”回调收集起来；插件包与加载器由测试自己放进去。
/// </summary>
public sealed class PluginManagerHarness : IDisposable
{
    private readonly string _root;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _reports = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _removed = new();

    public PluginManagerHarness(PluginManagerOptions? options = null)
    {
        _root = Path.Combine(Path.GetTempPath(), "hosihikari-testing", Guid.NewGuid().ToString("N"));
        PluginsRoot = Path.Combine(_root, "plugins");
        DataRoot = Path.Combine(_root, "data");
        ConfigRoot = Path.Combine(_root, "config", "hosihikari");
        Directory.CreateDirectory(PluginsRoot);
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(ConfigRoot);

        Manager = new PluginManager(
            PluginsRoot,
            DataRoot,
            ConfigRoot,
            report => _reports.Enqueue(report),
            carrierServices: null,
            onRemoved: name => _removed.Enqueue(name),
            options ?? new PluginManagerOptions());
    }

    public string Root => _root;
    public string PluginsRoot { get; }
    public string DataRoot { get; }
    public string ConfigRoot { get; }
    public PluginManager Manager { get; }
    public IReadOnlyList<string> Reports => _reports.ToArray();
    public IReadOnlyList<string> Removed => _removed.ToArray();

    /// <summary>在 plugins/&lt;name&gt; 下写清单与可选文件，返回包目录。</summary>
    public string CreatePackage(
        string name,
        string manifestJson,
        params (string Path, string Content)[] files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);
        if (Path.IsPathRooted(name)
            || name is "." or ".."
            || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException("测试包名必须是单个目录名", nameof(name));
        }

        var directory = Path.GetFullPath(Path.Combine(PluginsRoot, name));
        var pluginRoot = Path.GetFullPath(PluginsRoot) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(pluginRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException("测试包目录越界", nameof(name));
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifestJson);
        foreach (var (path, content) in files)
        {
            var target = Path.GetFullPath(Path.Combine(directory, path));
            if (!target.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !string.Equals(target, Path.GetFullPath(directory), StringComparison.Ordinal))
            {
                throw new ArgumentException($"测试包路径越界：{path}", nameof(files));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }

        return directory;
    }

    public bool Load(string name) => Manager.Load(Path.Combine(PluginsRoot, name));

    public void Dispose()
    {
        foreach (var name in Manager.LoadedNames.Reverse())
        {
            try
            {
                Manager.Unload(name);
            }
            catch
            {
                // 测试清理不掩盖原来的失败。
            }
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
