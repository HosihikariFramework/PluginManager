using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Hosihikari;
using Hosihikari.Internal;
using Hosihikari.Internal.Pack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hosihikari.Spike;

internal static class RegressionChecks
{
    internal static IEnumerable<(string Name, bool Passed, string Detail)> Run(string root)
    {
        foreach (var mode in new[] { "load", "enable", "null", "loader-throw", "cleanup-throw", "fault-on-load" })
        {
            var directory = Path.Combine(root, mode);
            var host = NewHost(directory);
            var regressionLoader = new FailingLoader(mode);
            host.RegisterLoader(regressionLoader);
            var package = Stage(directory, "Broken");
            var loaded = mode == "enable" ? LoadAll() : host.Load(package);
            host.Events.Publish("after failure");
            var context = regressionLoader.Context!;
            var scope = (PluginScope)context.Events;
            var expectedHooks = mode is "load" or "enable" or "cleanup-throw" or "fault-on-load";
            var hooksCleaned = regressionLoader.Unloads == (expectedHooks ? 1 : 0)
                               && regressionLoader.Disables == (mode == "enable" ? 1 : 0);
            var clean = !loaded && host.LoadedNames.Count == 0 && regressionLoader.Events == 0
                        && context.PluginServices.Get<Marker>() is null && hooksCleaned
                        && regressionLoader.Releases == (expectedHooks ? 1 : 0)
                        && RejectsReleasedScope(scope);
            yield return ($"R1 加载回滚 ({mode})", clean,
                $"events={regressionLoader.Events} unload={regressionLoader.Unloads} "
                + $"disable={regressionLoader.Disables} release={regressionLoader.Releases}");

            bool LoadAll()
            {
                host.LoadAll();
                return host.LoadedNames.Contains("Broken");
            }
        }

        var dependencyRoot = Path.Combine(root, "dependencies");
        var dependencyHost = NewHost(dependencyRoot);
        dependencyHost.RegisterLoader(new FailingLoader("load"));
        Stage(dependencyRoot, "Broken");
        Stage(dependencyRoot, "Dependent", "Broken");
        Stage(dependencyRoot, "Transitive", "Dependent");
        Stage(dependencyRoot, "Independent");
        dependencyHost.LoadAll();
        yield return ("R2 失败依赖跳过下游，独立分支继续", dependencyHost.LoadedNames.SequenceEqual(["Independent"]),
            string.Join(",", dependencyHost.LoadedNames));

        var configRoot = Path.Combine(root, "configuration");
        var configHost = NewHost(configRoot);
        configHost.RegisterLoader(new FailingLoader("none"));
        var badConfig = Stage(configRoot, "BadConfig");
        File.WriteAllText(Path.Combine(badConfig, "BadConfig.conf"), "foo = {");
        Stage(configRoot, "GoodConfig");
        var isolated = true;
        try { configHost.LoadAll(); }
        catch { isolated = false; }
        yield return ("R3 坏配置只拒绝一个包", isolated && configHost.LoadedNames.SequenceEqual(["GoodConfig"]),
            string.Join(",", configHost.LoadedNames));

        var bus = new EventBus(NullLogger.Instance);
        var weak = SubscribeCollectibleEvent(bus);
        Collect();
        yield return ("R4 最后一次退订释放事件类型", !weak.IsAlive, $"assembly alive={weak.IsAlive}");
        GC.KeepAlive(bus);

        var scheduler = new CountingScheduler();
        var scopeForGrowth = new PluginScope(new EventBus(NullLogger.Instance), scheduler, new PluginServiceRegistry());
        DisposeTasks(scopeForGrowth);
        Collect();
        var retained = scheduler.References.Count(reference => reference.IsAlive);
        yield return ("R5 已释放句柄不累积", retained == 0 && scheduler.Disposed == 1000,
            $"retained={retained} disposed={scheduler.Disposed}");
        GC.KeepAlive(scopeForGrowth);

        var concurrentScheduler = new CountingScheduler();
        var registry = new PluginServiceRegistry();
        var concurrentScope = new PluginScope(new EventBus(NullLogger.Instance), concurrentScheduler, registry);
        Parallel.For(0, 2000, i =>
        {
            try
            {
                var handle = concurrentScope.ScheduleOnce(TimeSpan.FromHours(1), () => { });
                if (i % 2 == 0) handle.Dispose();
                if (i % 7 == 0) concurrentScope.Register(new Marker());
                if (i == 100) concurrentScope.ReleaseAll();
                handle.Dispose();
            }
            catch (ObjectDisposedException) { }
        });
        concurrentScope.ReleaseAll();
        yield return ("R6 并发释放与登记不遗留资源", concurrentScheduler.Created == concurrentScheduler.Disposed
            && concurrentScheduler.DuplicateDisposes == 0 && registry.Get<Marker>() is null
            && RejectsReleasedScope(concurrentScope),
            $"created={concurrentScheduler.Created} disposed={concurrentScheduler.Disposed} duplicates={concurrentScheduler.DuplicateDisposes}");

        yield return ("R7 打包同时消费大量 stdout/stderr", PackOutput(Path.Combine(root, "pack")),
            "受控 publish 进程各输出 2MiB，10 秒内应退出");

        var manifestRoot = Path.Combine(root, "manifest");
        var manifestHost = NewHost(manifestRoot);
        manifestHost.RegisterLoader(new FailingLoader("none"));
        Stage(manifestRoot, "Good");
        Stage(manifestRoot, "SelfDependency", "SelfDependency");
        Stage(manifestRoot, "NullDependencies");
        StageManifest(manifestRoot, "NullDependencies", """
            {
              "name": "NullDependencies", "entry": "main", "type": "hosihikari",
              "dependencies": null,
              "extraInfo": { "api_version": "2", "manifest_version": "1", "loader": "regression" }
            }
            """);
        StageManifest(manifestRoot, "NullExtra", """
            {
              "name": "NullExtra", "entry": "main", "type": "hosihikari",
              "extraInfo": null
            }
            """);
        StageManifest(manifestRoot, "NullDependencyEntry", """
            {
              "name": "NullDependencyEntry", "entry": "main", "type": "hosihikari",
              "dependencies": [null],
              "extraInfo": { "api_version": "2", "manifest_version": "1", "loader": "regression" }
            }
            """);
        var manifestThrew = false;
        try { manifestHost.LoadAll(); }
        catch { manifestThrew = true; }
        var manifestLoaded = manifestHost.LoadedNames;
        yield return ("R8 清单 null 字段不中断整轮",
            !manifestThrew
            && manifestLoaded.Contains("Good")
            && !manifestLoaded.Contains("SelfDependency")
            && !manifestLoaded.Contains("NullExtra")
            && !manifestLoaded.Contains("NullDependencyEntry"),
            string.Join(",", manifestLoaded));

        var legacyRoot = Path.Combine(root, "legacy-keys");
        var legacyPackage = StageManifest(legacyRoot, "Legacy", """
            {
              "name": "Legacy", "entry": "main", "type": "hosihikari",
              "dependencies": [ { "name": "hosihikari" } ],
              "extraInfo": { "api": "2", "manifest_version": "1" }
            }
            """);
        var legacyReports = new List<string>();
        var legacyRead = ManifestReader.Read(legacyPackage, legacyReports.Add);
        var legacySource = Path.Combine(legacyRoot, "source");
        Directory.CreateDirectory(legacySource);
        var legacyManifestPath = Path.Combine(legacyRoot, "Legacy.json");
        File.WriteAllText(legacyManifestPath, File.ReadAllText(Path.Combine(legacyPackage, "manifest.json")));
        var legacyPack = Packager.Pack(legacySource, legacyManifestPath, Path.Combine(legacyRoot, "out"), []);
        yield return ("R20 旧清单键给出迁移错误",
            legacyRead is null
            && legacyReports.Any(message => message.Contains("已改名为 extraInfo.api_version", StringComparison.Ordinal))
            && !legacyPack.Succeeded
            && legacyPack.Problems.Any(problem => problem.Contains("已改名为 extraInfo.api_version", StringComparison.Ordinal)),
            legacyReports.FirstOrDefault() ?? string.Join(" | ", legacyPack.Problems));

        // R21 起是 ADR-0006 / 0013 / 0042 的规则：每条都同时看读取期与打包期。
        var ruleRoot = Path.Combine(root, "manifest-rules");
        var ruleSource = Path.Combine(ruleRoot, "source");
        Directory.CreateDirectory(ruleSource);
        File.WriteAllText(Path.Combine(ruleSource, "main"), "entry placeholder");
        string RuleManifest(string name, string extraInfo, string dependencies = """[ { "name": "hosihikari" } ]""")
        {
            var path = Path.Combine(ruleRoot, name + ".json");
            File.WriteAllText(path, $$"""
                {
                  "name": "{{name}}", "entry": "main", "type": "hosihikari", "version": "1.0.0",
                  "dependencies": {{dependencies}},
                  "extraInfo": {{extraInfo}}
                }
                """);
            return path;
        }

        var missingDependency = RuleManifest("NoFramework", """{ "api_version": "2", "manifest_version": "1", "loader": "regression" }""", "[]");
        var missingDependencyReports = new List<string>();
        var missingDependencyRead = ManifestReader.Read(
            StageManifest(ruleRoot, "NoFramework", File.ReadAllText(missingDependency)), missingDependencyReports.Add);
        var missingDependencyPack = Packager.Pack(ruleSource, missingDependency, Path.Combine(ruleRoot, "out-no-framework"), []);
        yield return ("R21 缺少 hosihikari 依赖时读取期与打包期都拒绝",
            missingDependencyRead is null
            && missingDependencyReports.Any(message => message.Contains("hosihikari", StringComparison.Ordinal))
            && !missingDependencyPack.Succeeded
            && missingDependencyPack.Problems.Any(problem => problem.Contains("hosihikari", StringComparison.Ordinal)),
            string.Join(" | ", missingDependencyReports.Concat(missingDependencyPack.Problems)));

        var carrierKind = RuleManifest(
            PluginContractInfo.ParentName,
            """{ "api_version": "2", "manifest_version": "1", "loader": "regression", "packageKind": "carrier" }""",
            "[]");
        var unknownKind = RuleManifest(
            "UnknownKind",
            """{ "api_version": "2", "manifest_version": "1", "loader": "regression", "packageKind": "pluginish" }""");
        var carrierPack = Packager.Pack(ruleSource, carrierKind, Path.Combine(ruleRoot, "out-carrier"), []);
        var unknownPack = Packager.Pack(ruleSource, unknownKind, Path.Combine(ruleRoot, "out-unknown"), []);
        yield return ("R22 packageKind 只认三个值，carrier 只给 hosihikari",
            carrierPack.Succeeded
            && !unknownPack.Succeeded
            && unknownPack.Problems.Any(problem => problem.Contains("packageKind", StringComparison.Ordinal)),
            string.Join(" | ", carrierPack.Problems.Concat(unknownPack.Problems)));

        var prepackSource = Path.Combine(ruleRoot, "prepack-source");
        Directory.CreateDirectory(prepackSource);
        var prepackGood = Path.Combine(ruleRoot, "prepack-good.json");
        File.WriteAllText(prepackGood, $$"""
            {
              "name": "Prepack", "entry": "main", "type": "hosihikari", "version": "1.0.0",
              "dependencies": [ { "name": "hosihikari" } ],
              "extraInfo": { "api_version": "2", "manifest_version": "1", "loader": "regression", "prepack": "{{(OperatingSystem.IsWindows() ? "cmd" : "sh")}}" }
            }
            """);
        var prepackBad = Path.Combine(ruleRoot, "prepack-bad.json");
        File.WriteAllText(prepackBad, $$"""
            {
              "name": "PrepackBad", "entry": "main", "type": "hosihikari", "version": "1.0.0",
              "dependencies": [ { "name": "hosihikari" } ],
              "extraInfo": { "api_version": "2", "manifest_version": "1", "loader": "regression", "prepack": "hosihikari-no-such-program" }
            }
            """);
        var prepackGoodPack = Packager.Pack(prepackSource, prepackGood, Path.Combine(ruleRoot, "out-prepack"), []);
        var prepackBadPack = Packager.Pack(prepackSource, prepackBad, Path.Combine(ruleRoot, "out-prepack-bad"), []);
        yield return ("R23 prepack 在打包期执行，失败拒包",
            prepackGoodPack.Succeeded
            && !prepackBadPack.Succeeded
            && prepackBadPack.Problems.Any(problem => problem.Contains("prepack", StringComparison.Ordinal)),
            string.Join(" | ", prepackGoodPack.Problems.Concat(prepackBadPack.Problems)));

        var lockRoot = Path.Combine(root, "load-lock");
        var parentLock = new object();
        var secondStarted = new ManualResetEventSlim();
        var secondRelease = new ManualResetEventSlim();
        var lockLoader = new FailingLoader("none")
        {
            SecondLoadStarted = secondStarted,
            SecondRelease = secondRelease,
        };
        var lockHost = new PluginManager(
            Path.Combine(lockRoot, "plugins"), Path.Combine(lockRoot, "data"), Path.Combine(lockRoot, "config"),
            _ => { lock (parentLock) { } });
        lockHost.RegisterLoader(lockLoader);
        Stage(lockRoot, "Probe");
        Stage(lockRoot, "Second", "Probe");
        var loadDone = new ManualResetEventSlim();
        var unloadDone = new ManualResetEventSlim();
        Exception? loadError = null;
        var loader = new Thread(() =>
        {
            try { lockHost.LoadAll(); }
            catch (Exception ex) { loadError = ex; }
            finally { loadDone.Set(); }
        });
        var unloader = new Thread(() =>
        {
            secondStarted.Wait(TimeSpan.FromSeconds(5));
            lock (parentLock)
            {
                secondRelease.Set();
                lockHost.Unload("Probe");
            }
            unloadDone.Set();
        });
        loader.Start();
        unloader.Start();
        var bothDone = loadDone.Wait(TimeSpan.FromSeconds(5)) && unloadDone.Wait(TimeSpan.FromSeconds(5));
        yield return ("R9 加载路径不在锁内回调父级", bothDone && secondStarted.IsSet && loadError is null,
            $"loaded={string.Join(",", lockHost.LoadedNames)} error={loadError?.Message}");

        var rollbackRoot = Path.Combine(root, "loader-rollback");
        var rollbackHost = NewHost(rollbackRoot);
        rollbackHost.RegisterLoader(new FailingLoader("loader-register-throw"));
        var brokenPackage = Stage(rollbackRoot, "Broken");
        var ghostPackage = StageManifest(rollbackRoot, "GhostUser", """
            {
              "name": "GhostUser", "entry": "main", "type": "hosihikari",
              "dependencies": [ { "name": "hosihikari" } ],
              "extraInfo": { "api_version": "2", "manifest_version": "1", "loader": "ghost" }
            }
            """);
        var brokenLoaded = rollbackHost.Load(brokenPackage);
        var ghostLoaded = rollbackHost.Load(ghostPackage);
        yield return ("R10 失败装载撤销 Loader 登记",
            !brokenLoaded && !ghostLoaded && rollbackHost.LoadedNames.Count == 0,
            $"broken={brokenLoaded} ghost={ghostLoaded} loaded={string.Join(",", rollbackHost.LoadedNames)}");

        var loaderRoot = Path.Combine(root, "loader-unload");
        var loaderHost = NewHost(loaderRoot);
        loaderHost.RegisterLoader(new FailingLoader("none") { RegisterLoader = true });
        var loaderPackage = Stage(loaderRoot, "LoaderPackage");
        var loaderLoaded = loaderHost.Load(loaderPackage);
        var loaderUnload = loaderHost.Unload("LoaderPackage");
        yield return ("R11 显式卸载 Loader 包被拒绝",
            loaderLoaded && !loaderUnload.Released && loaderHost.LoadedNames.Contains("LoaderPackage"),
            loaderUnload.Error ?? "卸载竟然成功");

        if (!OperatingSystem.IsWindows())
        {
            var aclRoot = Path.Combine(root, "manifest-acl");
            var aclHost = NewHost(aclRoot);
            aclHost.RegisterLoader(new FailingLoader("none"));
            Stage(aclRoot, "Good");
            var lockedPackage = Stage(aclRoot, "Locked");
            File.SetUnixFileMode(Path.Combine(lockedPackage, "manifest.json"), UnixFileMode.None);
            var aclIsolated = true;
            try { aclHost.LoadAll(); }
            catch { aclIsolated = false; }
            yield return ("R12 读不了的清单只跳过该包",
                aclIsolated && aclHost.LoadedNames.SequenceEqual(["Good"]),
                string.Join(",", aclHost.LoadedNames));
        }

        var reuseRoot = Path.Combine(root, "loader-reuse-log");
        var reuseLock = new object();
        var reuseParentHeld = new ManualResetEventSlim();
        var reuseLogEntered = new ManualResetEventSlim();
        var reuseLogger = new BlockingLoggerFactory(() =>
        {
            reuseLogEntered.Set();
            lock (reuseLock) { }
        });
        var reuseHost = new PluginManager(
            Path.Combine(reuseRoot, "plugins"), Path.Combine(reuseRoot, "data"), Path.Combine(reuseRoot, "config"),
            message => Console.WriteLine($"[回归] {message}"),
            new SingleService(reuseLogger));
        reuseHost.RegisterLoader(new FailingLoader("none") { RegisterLoader = true });
        var reusePackage = Stage(reuseRoot, "LoaderPackage");
        var firstLoad = reuseHost.Load(reusePackage);
        UnloadResult? reuseUnload = null;
        var unloadThread = Task.Run(() =>
        {
            lock (reuseLock)
            {
                reuseParentHeld.Set();
                reuseLogEntered.Wait(TimeSpan.FromSeconds(2));
                reuseUnload = reuseHost.Unload("LoaderPackage");
            }
        });
        reuseParentHeld.Wait(TimeSpan.FromSeconds(2));
        var reuseLoad = Task.Run(() => reuseHost.Load(reusePackage));
        var completed = reuseLoad.Wait(TimeSpan.FromSeconds(5)) && unloadThread.Wait(TimeSpan.FromSeconds(5));
        yield return ("R13 Loader 复用日志不在核心锁内",
            firstLoad && completed && reuseLoad.Result && reuseUnload is { Released: false },
            completed ? $"reload={reuseLoad.Result} unload={reuseUnload?.Error}" : "超时：日志回调与卸载互相等");

        var versionRoot = Path.Combine(root, "dependency-versions");
        var versionHost = NewHost(versionRoot);
        versionHost.RegisterLoader(new FailingLoader("none"));
        StageVersioned(versionRoot, "Lib", "2.0.0");
        StageVersioned(versionRoot, "Lib1", "1.5.0");
        StageVersioned(versionRoot, "Lib12", "1.2.7");
        StageVersioned(versionRoot, "Exact", "1.0.0", ("Lib", "[2.0.0]"));
        StageVersioned(versionRoot, "Range", "1.0.0", ("Lib", "[1.0.0,3.0.0)"));
        StageVersioned(versionRoot, "Any", "1.0.0", ("Lib", null));
        StageVersioned(versionRoot, "Wrong", "1.0.0", ("Lib", "[3.0.0]"));
        StageVersioned(versionRoot, "MajorWildcard", "1.0.0", ("Lib1", "1.*"));
        StageVersioned(versionRoot, "MajorWildcardMiss", "1.0.0", ("Lib", "1.*"));
        StageVersioned(versionRoot, "MinorWildcard", "1.0.0", ("Lib12", "1.2.*"));
        StageVersioned(versionRoot, "MinorWildcardMiss", "1.0.0", ("Lib1", "1.2.*"));
        StageVersioned(versionRoot, "UntypedLib", null);
        StageVersioned(versionRoot, "NeedsUntyped", "1.0.0", ("UntypedLib", "[1.0.0,3.0.0)"));
        StageVersioned(versionRoot, "BadVersion", "not-semver");
        versionHost.LoadAll();
        var versionLoaded = versionHost.LoadedNames.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        yield return ("R14 依赖版本约束过滤",
            versionLoaded.SequenceEqual(
                ["Any", "Exact", "Lib", "Lib1", "Lib12", "MajorWildcard", "MinorWildcard", "Range", "UntypedLib"]),
            string.Join(",", versionLoaded));

        var parentVersionRoot = Path.Combine(root, "dependency-versions-parent");
        var parentVersionHost = NewHost(parentVersionRoot);
        parentVersionHost.RegisterLoader(new FailingLoader("none"));
        var versionLib = StageVersioned(parentVersionRoot, "Lib", "2.0.0");
        var needs2 = StageVersioned(parentVersionRoot, "Needs2", "1.0.0", ("Lib", "[2.0.0]"));
        var needs3 = StageVersioned(parentVersionRoot, "Needs3", "1.0.0", ("Lib", "[3.0.0]"));
        var needs2Star = StageVersioned(parentVersionRoot, "Needs2Star", "1.0.0", ("Lib", "2.*"));
        var needs1Star = StageVersioned(parentVersionRoot, "Needs1Star", "1.0.0", ("Lib", "1.*"));
        var bare = StageVersioned(parentVersionRoot, "Bare", "1.0.0", ("Lib", null));
        var parentLoaded = parentVersionHost.Load(versionLib)
                           && parentVersionHost.Load(needs2)
                           && !parentVersionHost.Load(needs3)
                           && parentVersionHost.Load(needs2Star)
                           && !parentVersionHost.Load(needs1Star)
                           && parentVersionHost.Load(bare);
        yield return ("R15 父级交付也检查依赖版本",
            parentLoaded && parentVersionHost.LoadedNames.OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(["Bare", "Lib", "Needs2", "Needs2Star"]),
            string.Join(",", parentVersionHost.LoadedNames));

        var packVersionRoot = Path.Combine(root, "pack-version");
        var packVersionSource = Path.Combine(packVersionRoot, "source");
        Directory.CreateDirectory(packVersionSource);
        var badVersionManifest = Path.Combine(packVersionRoot, "bad-version.json");
        File.WriteAllText(badVersionManifest, JsonSerializer.Serialize(new
        {
            name = "BadVersion", entry = "main.dll", type = "hosihikari", version = "not-semver",
            dependencies = new object[] { new { name = "hosihikari" } },
            extraInfo = new { api_version = PluginContractInfo.CurrentApiVersion, manifest_version = "1", loader = "assembly" },
        }));
        var badConstraintManifest = Path.Combine(packVersionRoot, "bad-constraint.json");
        File.WriteAllText(badConstraintManifest, JsonSerializer.Serialize(new
        {
            name = "BadConstraint", entry = "main.dll", type = "hosihikari", version = "1.0.0",
            dependencies = new object[] { new { name = "Lib", version = ">>1" }, new { name = "hosihikari" } },
            extraInfo = new { api_version = PluginContractInfo.CurrentApiVersion, manifest_version = "1", loader = "assembly" },
        }));
        var badVersionPack = Packager.Pack(
            packVersionSource, badVersionManifest, Path.Combine(packVersionRoot, "out-version"), []);
        var badConstraintPack = Packager.Pack(
            packVersionSource, badConstraintManifest, Path.Combine(packVersionRoot, "out-constraint"), []);
        yield return ("R16 打包拒绝非法版本与约束",
            !badVersionPack.Succeeded && !badConstraintPack.Succeeded
            && badVersionPack.Problems.Any(problem => problem.Contains("SemVer", StringComparison.Ordinal))
            && badConstraintPack.Problems.Any(problem => problem.Contains("SemVer", StringComparison.Ordinal)),
            string.Join(" | ", badVersionPack.Problems.Concat(badConstraintPack.Problems)));

        var nullDependencyManifest = Path.Combine(packVersionRoot, "null-dependency.json");
        File.WriteAllText(nullDependencyManifest, """
            {
              "name": "NullDependency",
              "entry": "main.dll",
              "type": "hosihikari",
              "version": "1.0.0",
              "dependencies": [ null ],
              "extraInfo": { "api_version": 2, "manifest_version": "1", "loader": "assembly" }
            }
            """);
        PackResult? nullDependencyPack = null;
        string? nullDependencyError = null;
        try
        {
            nullDependencyPack = Packager.Pack(
                packVersionSource, nullDependencyManifest, Path.Combine(packVersionRoot, "out-null-dep"), []);
        }
        catch (Exception ex)
        {
            nullDependencyError = $"{ex.GetType().Name}: {ex.Message}";
        }

        yield return ("R18 打包拒绝 null 依赖而不是抛出",
            nullDependencyError is null
            && nullDependencyPack is { Succeeded: false, Problems: { } nullProblems }
            && nullProblems.Any(problem => problem.Contains("dependencies", StringComparison.Ordinal)),
            nullDependencyError ?? string.Join(" | ", nullDependencyPack?.Problems ?? []));

        var timerProbe = ScheduleOnceRetentionProbe();
        yield return ("R19 单次定时任务完成后释放捕获对象", timerProbe.Passed, timerProbe.Detail);

        var fuzzRoot = Path.Combine(root, "manifest-fuzz");
        Directory.CreateDirectory(fuzzRoot);
        var fuzzRandom = new Random(20261021);
        var fuzzClean = true;
        string? fuzzError = null;
        for (var i = 0; i < 200; i++)
        {
            var directory = Path.Combine(fuzzRoot, "p" + i);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "manifest.json"), RandomManifest(fuzzRandom));
            try
            {
                ManifestReader.Read(directory, _ => { });
            }
            catch (Exception ex)
            {
                fuzzClean = false;
                fuzzError = $"{ex.GetType().Name}: {ex.Message}";
                break;
            }
        }
        yield return ("R17 畸形清单不抛出", fuzzClean, fuzzError ?? "200 份输入");
    }

    private static (bool Passed, string Detail) ScheduleOnceRetentionProbe()
    {
        var logger = NullLogger.Instance;
        var scope = new PluginScope(new EventBus(logger), new Scheduler(logger), new PluginServiceRegistry());
        scope.Open();

        using var done = new ManualResetEventSlim();
        scope.ScheduleOnce(TimeSpan.Zero, done.Set);
        if (!done.Wait(TimeSpan.FromSeconds(5)))
        {
            scope.ReleaseAll();
            return (false, "任务没有在限期内执行");
        }

        var ownedField = typeof(PluginScope).GetField(
            "_owned", BindingFlags.NonPublic | BindingFlags.Instance);
        var deadline = Stopwatch.StartNew();
        var ownedCount = -1;
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            var owned = ownedField?.GetValue(scope);
            ownedCount = owned is null
                ? -1
                : (int)(owned.GetType().GetProperty("Count")?.GetValue(owned) ?? -1);
            if (ownedCount == 0)
            {
                break;
            }

            Thread.Sleep(10);
        }

        scope.ReleaseAll();
        return (ownedCount == 0, $"owned={ownedCount}");
    }

    private static PluginManager NewHost(string root) => new(
        Path.Combine(root, "plugins"), Path.Combine(root, "data"), Path.Combine(root, "config"),
        message => Console.WriteLine($"[回归] {message}"));

    private static string Stage(string root, string name, string? dependency = null)
    {
        var directory = Path.Combine(root, "plugins", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
        {
            name, entry = "main", type = "hosihikari",
            dependencies = dependency is null ? new object[] { new { name = "hosihikari" } } : new object[] { new { name = dependency }, new { name = "hosihikari" } },
            extraInfo = new { api_version = PluginContractInfo.CurrentApiVersion, manifest_version = "1", loader = "regression" },
        }));
        return directory;
    }

    private static string StageManifest(string root, string name, string json)
    {
        var directory = Path.Combine(root, "plugins", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), json);
        return directory;
    }

    private static string RandomManifest(Random random)
    {
        string[] fragments =
        [
            "{", "}", "[", "]", "\"name\"", ":", "\"p\"", "\"entry\"", "\"..\"", "\"main.dll\"",
            "\"type\"", "\"hosihikari\"", "\"version\"", "\"1.0.0\"", "\"dependencies\"",
            "null", "0", ",", "\\", "\n", "\"extraInfo\"", "\"api_version\"", "\"2\"",
        ];
        var builder = new StringBuilder();
        for (var i = 0; i < random.Next(1, 50); i++)
        {
            builder.Append(fragments[random.Next(fragments.Length)]);
        }

        return builder.ToString();
    }

    private static string StageVersioned(
        string root,
        string name,
        string? version,
        params (string Name, string? Version)[] dependencies) =>
        StageManifest(root, name, JsonSerializer.Serialize(new
        {
            name,
            entry = "main",
            type = "hosihikari",
            version,
            dependencies = dependencies.Select(dependency => new { dependency.Name, dependency.Version })
                .Cast<object>()
                .Append(new { Name = "hosihikari", Version = (string?)null })
                .ToArray(),
            extraInfo = new { api_version = PluginContractInfo.CurrentApiVersion, manifest_version = "1", loader = "regression" },
        }));

    private static bool RejectsReleasedScope(PluginScope scope)
    {
        Action[] actions = [() => scope.Subscribe<string>(_ => { }),
            () => scope.ScheduleOnce(TimeSpan.FromHours(1), () => { }),
            () => scope.Register(new Marker())];
        return actions.All(action =>
        {
            try { action(); return false; }
            catch (ObjectDisposedException) { return true; }
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SubscribeCollectibleEvent(EventBus bus)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("CollectibleRegression"),
            AssemblyBuilderAccess.RunAndCollect);
        var type = assembly.DefineDynamicModule("Events").DefineType("PluginEvent", TypeAttributes.Public).CreateType()!;
        var method = new DynamicMethod("Handle", typeof(void), [type]);
        method.GetILGenerator().Emit(OpCodes.Ret);
        var handler = method.CreateDelegate(typeof(Action<>).MakeGenericType(type));
        ((IDisposable)typeof(EventBus).GetMethod(nameof(EventBus.Subscribe))!
            .MakeGenericMethod(type).Invoke(bus, [handler])!).Dispose();
        return new WeakReference(type.Assembly);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DisposeTasks(PluginScope scope)
    {
        for (var i = 0; i < 1000; i++)
        {
            var handle = scope.ScheduleOnce(TimeSpan.FromHours(1), () => { });
            handle.Dispose();
            handle.Dispose();
        }
    }

    private static void Collect()
    {
        for (var i = 0; i < 10; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
    }

    private static bool PackOutput(string root)
    {
        var fixtureBin = Path.Combine(root, "bin");
        Directory.CreateDirectory(fixtureBin);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            File.Copy(file, Path.Combine(fixtureBin, Path.GetFileName(file)), true);
        var apphost = Path.Combine(AppContext.BaseDirectory,
            "Hosihikari.PluginManager.Host" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        var fakeDotnet = Path.Combine(fixtureBin, "dotnet" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        File.Copy(apphost, fakeDotnet, true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fakeDotnet, File.GetUnixFileMode(apphost));
        var start = new ProcessStartInfo(apphost) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--pack-output-regression");
        start.ArgumentList.Add(root);
        start.Environment["PATH"] = fixtureBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["HOSHI_PACK_OUTPUT_FIXTURE"] = "1";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            return false;
        }
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        return process.ExitCode == 0;
    }

    internal static int PackOutputChild(string root)
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "src"));
        Directory.CreateDirectory(Path.Combine(source, "shared"));
        File.WriteAllText(Path.Combine(source, "src", "main.cs"), """
            #:include "../shared/*.cs"
            #:package fixture@1.0.0
            public static class Entry { }
            """);
        File.WriteAllText(Path.Combine(source, "shared", "helper.cs"), "public static class Helper { }");
        var manifest = Path.Combine(root, "manifest.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            name = "OutputFixture", type = "hosihikari", entry = "src/main.cs",
            dependencies = new object[] { new { name = "hosihikari" } },
            extraInfo = new { api_version = PluginContractInfo.CurrentApiVersion, manifest_version = "1", loader = "csharp" },
        }));
        var result = Packager.Pack(source, manifest, Path.Combine(root, "packages"), []);
        return result.Succeeded
               && result.PackageDirectory is { } package
               && File.Exists(Path.Combine(package, "shared", "helper.cs"))
            ? 0
            : 1;
    }

    private sealed class Marker { }

    private sealed class SingleService(ILoggerFactory loggerFactory) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ILoggerFactory) ? loggerFactory : null;
    }

    private sealed class BlockingLoggerFactory(Action onLog) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new BlockingLogger(onLog);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class BlockingLogger(Action onLog) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) => onLog();
        }
    }

    private sealed class GhostLoader : IPluginLoader
    {
        public string Id => "ghost";
        public bool CanLoad(PluginManifest manifest) => manifest.Loader == Id;
        public PluginHandle? Load(PluginManifest manifest, string packageDirectory, IPluginContext context) =>
            new TestPluginHandle(new GhostPlugin());

        private sealed class GhostPlugin : IPlugin
        {
            public void OnLoad(IPluginContext context) { }
            public PluginHookResult OnEnable() => PluginHookResult.Allow();
            public PluginHookResult OnDisable() => PluginHookResult.Allow();
            public void OnUnload() { }
        }
    }

    private sealed class FailingLoader(string mode) : IPluginLoader
    {
        private readonly string _mode = mode;
        public string Id => "regression";
        public IPluginContext? Context;
        public ManualResetEventSlim? SecondLoadStarted;
        public ManualResetEventSlim? SecondRelease;
        public bool RegisterLoader { get; set; }
        public int Events, Unloads, Disables, Releases;
        public bool CanLoad(PluginManifest manifest) => manifest.Loader == Id;
        public PluginHandle? Load(PluginManifest manifest, string packageDirectory, IPluginContext context)
        {
            Context = context;
            if (manifest.Name == "Second")
            {
                SecondLoadStarted?.Set();
                SecondRelease?.Wait(TimeSpan.FromSeconds(5));
                return null;
            }
            if (manifest.Name == "Broken" && _mode is "null" or "loader-throw")
            {
                UseContext(context);
                if (_mode == "loader-throw") throw new InvalidOperationException("loader failure");
                return null;
            }
            return new Instance(new Plugin(this, manifest.Name), this);
        }
        private void UseContext(IPluginContext context)
        {
            context.Events.Subscribe<string>(_ => Interlocked.Increment(ref Events));
            context.PluginServices.Register(new Marker());
            context.Scheduler.ScheduleOnce(TimeSpan.FromHours(1), () => Interlocked.Increment(ref Events));
        }
        private sealed class Instance(IPlugin plugin, FailingLoader owner) : PluginHandle(plugin)
        {
            public override bool IsUnloaded => Plugin is null;

            public override void Unload()
            {
                if (Plugin is null)
                {
                    return;
                }

                owner.Releases++;
                Plugin = null;
            }

            public override Task<bool> WaitForUnloadAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(IsUnloaded);
        }
        private sealed class Plugin(FailingLoader owner, string name) : IPlugin
        {
            public void OnLoad(IPluginContext context)
            {
                owner.UseContext(context);
                if (owner.RegisterLoader)
                {
                    context.RegisterLoader(new GhostLoader());
                }
                if (name == "Broken" && owner._mode is "load" or "cleanup-throw")
                    throw new InvalidOperationException("load failure");
                if (name == "Broken" && owner._mode == "fault-on-load")
                    context.ReportFault("immediate death");
                if (name == "Broken" && owner._mode == "loader-register-throw")
                {
                    context.RegisterLoader(new GhostLoader());
                    throw new InvalidOperationException("loader registration then failure");
                }
            }
            public PluginHookResult OnEnable() => name == "Broken" && owner._mode == "enable"
                ? throw new InvalidOperationException("enable failure") : PluginHookResult.Allow();
            public PluginHookResult OnDisable() { owner.Disables++; return PluginHookResult.Allow(); }
            public void OnUnload()
            {
                owner.Unloads++;
                if (owner._mode == "cleanup-throw") throw new InvalidOperationException("unload failure");
            }
        }
    }

    private sealed class CountingScheduler : IScheduler
    {
        internal readonly ConcurrentBag<WeakReference> References = [];
        internal int Created, Disposed, DuplicateDisposes;
        public IDisposable ScheduleOnce(TimeSpan delay, Action work)
        {
            Interlocked.Increment(ref Created);
            var disposable = new Counted(this);
            References.Add(new WeakReference(disposable));
            return disposable;
        }
        public IDisposable SchedulePeriodic(TimeSpan interval, Action work) => ScheduleOnce(interval, work);
        private sealed class Counted(CountingScheduler owner) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Increment(ref owner.Disposed);
                else Interlocked.Increment(ref owner.DuplicateDisposes);
            }
        }
    }
}
