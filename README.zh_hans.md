# Hosihikari.PluginManager

[English](README.md) | [简体中文](README.zh_hans.md)

星光框架的**插件管理器**：把插件包发现出来、按依赖图装载、各自关进一个可回收的 ALC、驱动它的生命周期，并在任何一环失败时只让它自己倒下。

插件契约在另一个仓与另一个包：[`Hosihikari.Plugin.Abstractions`](https://www.nuget.org/packages/Hosihikari.Plugin.Abstractions)。

## 这个仓里有什么

| | |
|---|---|
| `src/` | 插件管理器：发现、依赖图、程序集型加载器、生命周期、失败隔离 |
| `tools/Pack/` | 打包工具：装配插件包，并在打包期拦下几条报错点离病因很远的规则 |
| `test/Foo`、`test/Bar` | 两个测试插件 —— 它们也是打包工具的头两个用户 |
| `host/` | 断言宿主：普通 .NET 控制台，**不碰 BDS、Shim、任何载体** |

## 跑一遍

```
dotnet run --project host/Hosihikari.PluginManager.Host.csproj -c Release
```

它把整份断言清单跑一遍，任一条不过就以非零退出。**必须 `-c Release`** —— Debug 保留局部变量引用，会让卸载验证假失败。

CI 跑的就是这一条命令，走的也是外部插件作者拿到契约的那条路（从 nuget.org 取包）。

## 契约怎么被拿到

`src/` 与 `test/` 里的项目对 `Hosihikari.Plugin.Abstractions` 有两种拿法，按兄弟目录在不在自动择一：

- **两个仓并列摆放** → `ProjectReference`。改契约不用重新打包。
- **只检出本仓**（CI、以及任何外部消费者）→ `PackageReference`，版本精确钉住。跟着契约的 API 面走。

本机库的 RID 优先解析走 McMaster 的 fork；它也必须作为兄弟目录检出（CI 就是这么摆的）。

## 配置与状态

构造函数重载接受 `PluginManagerOptions`。`UnloadVerificationTimeout` 默认 30 秒；有限值必须为正且
不超过约 49.7 天，`Timeout.InfiniteTimeSpan` 关闭计时器。超时按卸载失败记录，报告会如实写明；
槽位保持 `Detached`，因此 `Plugins` 仍会返回它。

```csharp
var options = new PluginManagerOptions
{
    UnloadVerificationTimeout = TimeSpan.FromSeconds(60),
};

var manager = new PluginManager(
    pluginsRoot, dataRoot, configRoot, reportFault,
    carrierServices, onRemoved, options);
```

`Plugins` 返回所有槽位的只读快照，包含 `Detached`；`TryGetPlugin(name, out var info)` 按名字读取
一个。`PluginInfo` 带 `Name`、`Version`、`Loader`、`State`、`Enabled`、`IsLoader` 与 `Dependencies`。

`loggerFactory` 由宿主提供：

```csharp
var log = loggerFactory.CreateLogger("PluginManager");

foreach (var info in manager.Plugins)
{
    log.LogInformation(
        "{Name} {Version} loader={Loader} state={State} enabled={Enabled}",
        info.Name, info.Version, info.Loader, info.State, info.Enabled);
}
```

## 作为 lip 包

打 `v*` 标签会构建 `hosihikari-core-<版本>.zip`，发布为
`github.com/HosihikariFramework/PluginManager`。包把插件管理器装进 `plugins/hosihikari/`；
原生 Shim 包用 lip 依赖声明兼容的 Core 版本。原生侧与托管侧在启动时核对
`HostingContractVersion`。

源码部署时，`bash ./pack.sh <版本>` 可以在本地构建同一份归档。

## 不在这里的

- **Shim** —— 把 CLR 托管进 BDS 进程的那层原生代码，是独立的仓。
- **进程外桥** —— RpcLoader 是官方加载器，独立成仓，插件在子进程中运行；插件管理器只认 `IPluginLoader` 契约，不认识子进程的内部。
- **任何 BDS 专有能力** —— 世界、实体、命令、游戏事件都不在能力面内。

## 许可

LGPL-3.0-or-later，见 [LICENSE.md](LICENSE.md)。
