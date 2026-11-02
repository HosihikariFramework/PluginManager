using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hosihikari;

namespace Hosihikari.Internal.Pack;

/// <summary>一次打包的结果。</summary>
/// <param name="Succeeded">是否成功。</param>
/// <param name="PackageDirectory">成功时的包目录。</param>
/// <param name="Problems">失败原因，逐条可读。</param>
public readonly record struct PackResult(bool Succeeded, string? PackageDirectory, IReadOnlyList<string> Problems);

/// <summary>
/// 把一份构建输出 + 一份清单装配成插件包，并在这里检查那些**报错点离病因很远**的规则。
/// 作者手写清单仍然可以跑得起来，但会在运行期以难以归因的方式失败（ADR-0004 / 0006 / 0008 / 0012）。
/// </summary>
public static class Packager
{
    /// <summary>
    /// 装配一个包。
    ///
    /// <paramref name="sharedLayer"/> 是目标载体的共享层程序集名清单，与运行期
    /// <c>AssemblyPluginLoader</c> 拿到的那份必须同源（ADR-0021 / 0026）。这里要的是文件名，
    /// 因为打包期只看得见构建输出里的文件名。
    /// </summary>
    public static PackResult Pack(
        string sourceDirectory,
        string manifestPath,
        string outputRoot,
        IReadOnlyList<string> sharedLayer)
    {
        var problems = new List<string>();

        PluginManifest? manifest = null;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            problems.Add($"清单读不出来：{ex.Message}");
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Name))
        {
            problems.Add("清单缺少 name");
            return new PackResult(false, null, problems);
        }

        // name 直接参与包目录拼装，必须是一个目录名 —— 分隔符、. 与 .. 会跑出输出根。
        if (manifest.Name is "." or ".."
            || manifest.Name.EndsWith('.') || manifest.Name.EndsWith(' ')
            || manifest.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            problems.Add($"清单 name 必须是单个目录名：{manifest.Name}");
            return new PackResult(false, null, problems);
        }

        if (ManifestReader.LegacyExtraInfoKeys(manifest) is { } legacy)
        {
            problems.Add(CoreText.Get("Manifest_LegacyFieldBody", legacy.Old, legacy.New));
        }

        if (!DependencyVersion.TryValidateManifest(manifest, out var versionProblem))
        {
            problems.Add(versionProblem!);
        }

        // 包目录名就是插件名 —— 目录名与清单 name 一致这条由这里保证（ADR-0006）。
        var packageDirectory = Path.Combine(outputRoot, manifest.Name);

        // 递归复制（ADR-0034）要求输出与构建输出不相交；否则复制会把自己再枚举进来。
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        var packageRoot = Path.GetFullPath(packageDirectory);
        if (IsSameOrUnder(packageRoot, sourceRoot) || IsSameOrUnder(sourceRoot, packageRoot))
        {
            problems.Add($"打包输出不能与构建输出相交：{packageRoot} / {sourceRoot}");
            return new PackResult(false, null, problems);
        }

        // 契约主版本（ADR-0012）。
        if (manifest.ApiVersion != PluginContractInfo.CurrentApiVersion)
        {
            problems.Add($"清单 api_version 是 {manifest.ApiVersion ?? "未声明"}，当前契约是 {PluginContractInfo.CurrentApiVersion}");
        }

        // 清单 schema 版本（ADR-0012）。
        if (manifest.ManifestVersion != PluginContractInfo.CurrentManifestVersion)
        {
            problems.Add($"清单 manifest_version 是 {manifest.ManifestVersion ?? "未声明"}，当前是 {PluginContractInfo.CurrentManifestVersion}");
        }

        // type 是父级的路由键（ADR-0006）。写错的包在父级态下根本到不了我们手里，
        // 而且失败点离病因很远 —— 所以在这里就拦下。
        if (manifest.ParentName != PluginContractInfo.ParentName)
        {
            problems.Add($"清单 type 必须是 {PluginContractInfo.ParentName}，实际 {(string.IsNullOrWhiteSpace(manifest.ParentName) ? "未声明" : manifest.ParentName)}");
        }

        // 共享层副本（ADR-0004 / 0019）—— 这条是静默失败的那条，所以放在打包期拦。
        // 注意它可能是**传递**来的：作者只直接引用契约，而契约又牵进别的共享层程序集时
        // 一样会出现在构建输出里，所以这里对整份输出逐文件比对，不看是谁带进来的。
        var offenders = Directory
            .EnumerateFiles(sourceDirectory, "*.dll", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(sourceDirectory, file))
            .Where(file => sharedLayer.Contains(Path.GetFileName(file)))
            .ToArray();
        if (offenders.Length > 0)
        {
            problems.Add(
                $"构建输出里带了共享层程序集（{string.Join(", ", offenders)}）—— " +
                "引用它们时要 ExcludeAssets=\"runtime\"，否则插件包里是第二个类型身份");
        }

        // 入口必须在包内（ADR-0006）。出界时后面的存在性与入口声明检查都不做 ——
        // 它们会去读包外的文件。
        if (!ManifestReader.IsEntryContained(sourceDirectory, manifest.EntryPath))
        {
            problems.Add($"清单 entry 必须是包内相对路径（实际 {manifest.EntryPath}）");
        }
        // 入口（ADR-0008）。编译器已经保证它与 IPlugin 和有无参构造是一致的，这里只确认它存在。
        else if (manifest.Loader == "assembly")
        {
            var entryPath = Path.Combine(sourceDirectory, manifest.EntryPath);
            if (!File.Exists(entryPath))
            {
                problems.Add($"清单 entry 指向的 {manifest.EntryPath} 不在构建输出里");
            }
            else if (!DeclaresEntryPoint(entryPath, problems))
            {
                // DeclaresEntryPoint 自己填了原因
            }
        }

        // file-based app 风格的 #:package 与 extraInfo.packages 一起在打包期还原。
        var packages = ReadPackages(manifest, sourceDirectory, problems);
        if (problems.Count > 0)
        {
            return new PackResult(false, null, problems);
        }

        // 先装到临时目录，成功后再替换 —— 还原失败不会留下一个能被 LoadAll 捡走的半包。
        var staging = packageDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDirectory, file);
                var target = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }

            File.Copy(manifestPath, Path.Combine(staging, "manifest.json"), overwrite: true);

            // 清单里声明的第三方包在**这里**还原 —— 供应链留在打包那一刻，服务端加载时不联网。
            if (!RestorePackages(packages, staging, sharedLayer, problems))
            {
                return new PackResult(false, null, problems);
            }

            if (Directory.Exists(packageDirectory))
            {
                Directory.Delete(packageDirectory, recursive: true);
            }

            Directory.Move(staging, packageDirectory);
            return new PackResult(true, packageDirectory, []);
        }
        catch (Exception ex)
        {
            problems.Add($"写入包目录失败：{ex.Message}");
            return new PackResult(false, null, problems);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 清理失败不影响结果。
                }
            }
        }
    }

    /// <summary>
    /// 收集清单 <c>extraInfo.packages</c>（逗号分隔的 <c>Id/Version</c>）与 C# 入口里的
    /// <c>#:package Id@Version</c>。NuGet 只在打包期还原，服务端加载时不联网。
    /// </summary>
    private static List<string> ReadPackages(
        PluginManifest manifest,
        string sourceDirectory,
        List<string> problems)
    {
        var packages = new List<string>();
        if (manifest.ExtraInfo.TryGetValue("packages", out var packagesValue)
            && packagesValue.ValueKind == JsonValueKind.String
            && packagesValue.GetString() is { Length: > 0 } declared)
        {
            packages.AddRange(declared.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (manifest.Loader == "csharp")
        {
            ReadSourcePackages(sourceDirectory, manifest, packages, problems);
        }

        return packages.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ReadSourcePackages(
        string sourceDirectory,
        PluginManifest manifest,
        List<string> packages,
        List<string> problems)
    {
        var root = Path.GetFullPath(sourceDirectory);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var visited = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        string Full(string path)
        {
            var full = Path.GetFullPath(Path.Combine(root, path));
            if (!full.StartsWith(prefix, comparison))
            {
                problems.Add($"#:include 出界：{path}");
            }

            return full;
        }

        void Visit(string path)
        {
            var full = Full(path);
            if (!full.StartsWith(prefix, comparison) || !visited.Add(full))
            {
                return;
            }

            if (!File.Exists(full))
            {
                problems.Add($"#:include 指向的 {path} 不存在");
                return;
            }

            if (!string.Equals(Path.GetExtension(full), ".cs", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"#:include 只支持 .cs：{path}");
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"读不了源码 {path}：{ex.Message}");
                return;
            }

            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                if (IsDirective(trimmed, "#:package"))
                {
                    var value = trimmed["#:package".Length..].Trim();
                    var separator = value.IndexOf('@');
                    var id = separator < 0 ? value : value[..separator];
                    var version = separator < 0 ? "*" : value[(separator + 1)..];
                    if (id.Length == 0)
                    {
                        problems.Add($"{path}: #:package 缺少包名");
                        continue;
                    }

                    if (version.Length == 0)
                    {
                        version = "*";
                    }

                    packages.Add($"{id}/{version}");
                }
                else if (IsDirective(trimmed, "#:include"))
                {
                    var include = trimmed["#:include".Length..].Trim().Trim('"');
                    if (include.Length == 0)
                    {
                        problems.Add($"{path}: #:include 缺少路径");
                    }
                    else
                    {
                        var baseDirectory = Path.GetDirectoryName(path) ?? "";
                        if (include.IndexOfAny(['*', '?']) >= 0)
                        {
                            var fullBase = Path.Combine(root, baseDirectory);
                            if (!Directory.Exists(fullBase))
                            {
                                problems.Add($"{path}: #:include 的目录不存在：{include}");
                                continue;
                            }

                            var matches = ExpandGlob(root, fullBase, include);
                            if (matches.Length == 0)
                            {
                                problems.Add($"{path}: #:include 没有匹配到文件：{include}");
                                continue;
                            }

                            foreach (var match in matches)
                            {
                                Visit(match);
                            }
                        }
                        else
                        {
                            Visit(Path.Combine(baseDirectory, include));
                        }
                    }
                }
            }
        }

        var entry = manifest.EntryPath;
        var entryPath = Full(entry);
        if (entryPath.StartsWith(prefix, comparison) && !File.Exists(entryPath))
        {
            problems.Add($"清单 entry 指向的 {entry} 不在构建输出里");
        }
        else
        {
            Visit(entry);
        }
    }

    private static bool IsDirective(string line, string directive) =>
        line.StartsWith(directive, StringComparison.Ordinal)
        && (line.Length == directive.Length || char.IsWhiteSpace(line[directive.Length]));

    private static string[] ExpandGlob(string packageRoot, string baseDirectory, string pattern)
    {
        // 从包根枚举：相对路径里可以带 ..，按模板目录枚举会漏掉兄弟目录。
        var normalized = pattern.Replace('\\', '/');
        var regex = new Regex(GlobToRegex(normalized), RegexOptions.CultureInvariant);
        return Directory
            .EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(file => regex.IsMatch(Path.GetRelativePath(baseDirectory, file).Replace('\\', '/')))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();
    }

    private static string GlobToRegex(string pattern)
    {
        var builder = new System.Text.StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                    {
                        index++;
                        builder.Append("(?:.*/)?");
                    }
                    else
                    {
                        builder.Append(".*");
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }
            }
            else if (character == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(character.ToString()));
            }
        }

        return builder.Append('$').ToString();
    }

    /// <summary>
    /// 按已收集的 <c>Id/Version</c> 还原第三方包，把它们的 dll 放进包里。
    /// 依赖闭包交给 SDK 解析，打包工具不自己碰 NuGet。
    /// </summary>
    private static bool RestorePackages(
        IReadOnlyList<string> references,
        string packageDirectory,
        IReadOnlyList<string> sharedLayer,
        List<string> problems)
    {
        if (references.Count == 0)
        {
            return true;
        }

        var workspace = Path.Combine(Path.GetTempPath(), "hosihikari-pack-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workspace);

            var items = string.Join(
                Environment.NewLine,
                references.Select(reference =>
                {
                    var parts = reference.Split('/', 2);
                    var version = parts.Length > 1 ? parts[1] : "*";
                    return $"    <PackageReference Include=\"{parts[0]}\" Version=\"{version}\" />";
                }));

            var project = Path.Combine(workspace, "restore.csproj");
            File.WriteAllText(
                project,
                "<Project Sdk=\"Microsoft.NET.Sdk\">" + Environment.NewLine
                + "  <PropertyGroup>" + Environment.NewLine
                + "    <TargetFramework>net10.0</TargetFramework>" + Environment.NewLine
                + "    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>" + Environment.NewLine
                + "  </PropertyGroup>" + Environment.NewLine
                + "  <ItemGroup>" + Environment.NewLine
                + items + Environment.NewLine
                + "  </ItemGroup>" + Environment.NewLine
                + "</Project>" + Environment.NewLine);

            var output = Path.Combine(workspace, "out");
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "publish", project, "-c", "Release", "-o", output, "--nologo", "-v", "q" })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WhenAll(outputTask, errorTask).GetAwaiter().GetResult();
            var errorText = errorTask.Result;
            if (process.ExitCode != 0)
            {
                problems.Add($"还原第三方包失败（{string.Join(", ", references)}）：{errorText.Trim()}");
                return false;
            }

            // 共享层的程序集不随包走（ADR-0004）；restore 工程自己那份也没有意义。
            // 递归复制是为了保留 runtimes/<rid>/native 的本机资产（ADR-0034）。
            var restoreArtifacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "restore.dll", "restore.exe", "restore", "restore.pdb",
                "restore.deps.json", "restore.runtimeconfig.json",
            };
            foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (restoreArtifacts.Contains(name) || sharedLayer.Contains(name))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(output, file);
                var target = Path.Combine(packageDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }

            return true;
        }
        catch (Exception ex)
        {
            problems.Add($"还原第三方包失败（{string.Join(", ", references)}）：{ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workspace))
                {
                    Directory.Delete(workspace, recursive: true);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响结果。
            }
        }
    }

    /// <summary>
    /// 只读元数据，不把入口装进任何可执行上下文。
    ///
    /// `Assembly.LoadFrom` 做不到这一点：它把程序集装进**默认 ALC** 并且从此留在那里，
    /// 而在同一个进程里再测插件行为时就被它污染了 —— 插件 ALC 的解析在
    /// `PreferSharedTypes` 下会优先采纳"默认上下文里已经有了的那份"。
    /// </summary>
    private static bool DeclaresEntryPoint(string entryPath, List<string> problems)
    {
        try
        {
            var full = Path.GetFullPath(entryPath);
            var references = Directory.EnumerateFiles(Path.GetDirectoryName(full)!, "*.dll")
                // 契约不在构建输出里（复制进去会被这条规则自己拦下），所以显式补上。
                .Append(typeof(PluginContractInfo).Assembly.Location)
                .Concat(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator));

            using var metadata = new MetadataLoadContext(new PathAssemblyResolver(references));
            var inspection = metadata.LoadFromAssemblyPath(full);

            var declared = inspection.GetCustomAttributesData().Count(IsEntryPoint);
            if (declared != 1)
            {
                problems.Add($"{Path.GetFileName(entryPath)} 声明了 {declared} 个入口 —— 需要恰好一个 [assembly: EntryPoint<T>]");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            problems.Add($"读不了 {Path.GetFileName(entryPath)} 的入口声明：{ex.Message}");
            return false;
        }
    }

    /// <summary>path 等于 root，或落在 root 下面。</summary>
    private static bool IsSameOrUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return string.Equals(normalizedPath, normalizedRoot, comparison)
               || normalizedPath.StartsWith(prefix, comparison);
    }

    /// <summary>按名字认，因为在只读元数据里契约是另一套类型对象。</summary>
    private static bool IsEntryPoint(CustomAttributeData attribute)
    {
        for (var type = attribute.AttributeType; type is not null; type = type.BaseType)
        {
            if (type.FullName == typeof(EntryPointAttributeBase).FullName)
            {
                return true;
            }
        }

        return false;
    }
}
