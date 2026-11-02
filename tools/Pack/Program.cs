using Hosihikari;
using Hosihikari.Internal;
using Hosihikari.Internal.Pack;

// 用法：
//   Hosihikari.Plugin.Pack pack-loader <构建输出目录> <清单路径> <输出根目录>
//   Hosihikari.Plugin.Pack pack-plugin   <构建输出目录> <清单路径> <输出根目录>
//   Hosihikari.Plugin.Pack pack-plugin-from-assembly <入口程序集> <清单路径> <输出根目录>
//   Hosihikari.Plugin.Pack <构建输出目录> <清单路径> <输出根目录> [--shared <程序集文件名>]...
// pack-loader / pack-plugin 都用核心的共享层清单剔除共享层副本（ADR-0036）；
// 两个名字只是说明调用方，规则只有一份。显式清单那条留给自定义共享层的载体。
if (args.Length == 4 && (args[0] == "pack-loader" || args[0] == "pack-plugin"))
{
    return PackLoader(args[1], args[2], args[3]);
}

// MSBuild 的 $(TargetPath) 是文件路径，不会有 $(TargetDir) 的收尾反斜杠；
// 这条命令让 PackPlugin 目标避开 Windows 命令行转义陷阱。
if (args.Length == 4 && args[0] == "pack-plugin-from-assembly")
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(args[1]));
    if (directory is null)
    {
        Console.WriteLine(PackText.Get("EntryAssemblyDirectory", args[1]));
        return 2;
    }

    return PackLoader(directory, args[2], args[3]);
}

// --shared 可以重复，写的是**目标载体**的共享层清单 —— 它由载体声明，
// 与运行期 AssemblyPluginLoader 拿到的那份必须同源（ADR-0021 / 0026）。
if (args.Length < 3)
{
    Console.WriteLine(PackText.Get("Usage"));
    Console.WriteLine(PackText.Get("UsagePackLoader"));
    Console.WriteLine(PackText.Get("UsagePackPlugin"));
    Console.WriteLine(PackText.Get("UsagePackAssembly"));
    Console.WriteLine(PackText.Get("UsageShared"));
    return 2;
}

var sharedLayer = new List<string>();
for (var i = 3; i < args.Length; i++)
{
    if (args[i] != "--shared" || i + 1 >= args.Length)
    {
        Console.WriteLine(PackText.Get("UnrecognizedArgument", args[i]));
        return 2;
    }

    sharedLayer.Add(args[++i]);
}

if (sharedLayer.Count == 0)
{
    Console.WriteLine(PackText.Get("SharedRequired"));
    return 2;
}

var result = Packager.Pack(args[0], args[1], args[2], sharedLayer);
if (result.Succeeded)
{
    Console.WriteLine(PackText.Get("Packed", result.PackageDirectory!));
    return 0;
}

Console.WriteLine(PackText.Get("PackingFailed"));
foreach (var problem in result.Problems)
{
    Console.WriteLine($"- {problem}");
}

return 1;

// Loader 包的装配：先按核心的共享层清单剔除副本，再交给 Packager 做常规校验与复制。
// 共享清单只有这一份（AssemblyPluginLoader.SharedAssemblyFileNames），各加载器仓的 pack 脚本 不再各写一份。
static int PackLoader(string sourceDirectory, string manifestPath, string outputRoot)
{
    var shared = AssemblyPluginLoader.SharedAssemblyFileNames;
    var sharedBases = shared
        .Select(name => Path.GetFileNameWithoutExtension(name))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var staging = Path.Combine(Path.GetTempPath(), "hosihikari-pack-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(staging);
    try
    {
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            if (sharedBases.Contains(Path.GetFileNameWithoutExtension(file)))
            {
                continue;
            }

            var relative = Path.GetRelativePath(sourceDirectory, file);
            var target = Path.Combine(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        var result = Packager.Pack(staging, manifestPath, outputRoot, shared);
        if (result.Succeeded)
        {
            Console.WriteLine(PackText.Get("Packed", result.PackageDirectory!));
            return 0;
        }

        Console.WriteLine(PackText.Get("PackingFailed"));
        foreach (var problem in result.Problems)
        {
            Console.WriteLine($"- {problem}");
        }

        return 1;
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
                // 临时目录清不掉不影响已完成的包。
            }
        }
    }
}
