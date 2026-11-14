using Hosihikari;
using Hosihikari.Internal;
using Hosihikari.Internal.Pack;

// 用法：
//   Hosihikari.Plugin.Pack pack-loader <构建输出目录> <清单路径> <输出根目录> [--carrier <载体包目录>]
//   Hosihikari.Plugin.Pack pack-plugin   <构建输出目录> <清单路径> <输出根目录> [--carrier <载体包目录>]
//   Hosihikari.Plugin.Pack pack-plugin-from-assembly <入口程序集> <清单路径> <输出根目录>
//   Hosihikari.Plugin.Pack migrate <包目录>
//   Hosihikari.Plugin.Pack <构建输出目录> <清单路径> <输出根目录> [--shared <程序集文件名>]...
// MSBuild 目标没有自己的资源机制；用打包器按当前 UI 文化打印 target 的错误。
if (args.Length == 2 && args[0] == "message")
{
    Console.WriteLine(PackText.Get(args[1]));
    return 1;
}

// --help 走的是缺参数的用法路径，退出码一致为 2。
if (args.Length == 1 && args[0] is "--help" or "-h")
{
    PrintUsage();
    return 2;
}

// 旧包升级（ADR-0058）：读 manifest.json 写 hosihikari.json，然后删掉旧文件。
if (args.Length == 2 && args[0] == "migrate")
{
    var migrated = Packager.Migrate(args[1]);
    if (!migrated.Succeeded)
    {
        Console.WriteLine(PackText.Get("PackingFailed"));
        foreach (var problem in migrated.Problems)
        {
            Console.WriteLine($"- {problem}");
        }

        return 1;
    }

    Console.WriteLine(PackText.Get("Migrated", Path.Combine(args[1], Packager.ManifestFileName)));
    if (migrated.Notes.Count > 0)
    {
        Console.WriteLine(PackText.Get("MigrateNotes"));
        foreach (var note in migrated.Notes)
        {
            Console.WriteLine($"- {note}");
        }
    }

    return 0;
}

// pack-loader / pack-plugin 都用核心的共享层清单剔除共享层副本（ADR-0036）；
// 两个名字只是说明调用方，规则只有一份。显式清单那条留给自定义共享层的载体。
if (args.Length >= 4 && (args[0] == "pack-loader" || args[0] == "pack-plugin"))
{
    // --carrier 指到已装好的载体包，读它声明的共享层（ADR-0061）；不传就只剔基地清单。
    string? carrier = null;
    for (var i = 4; i < args.Length; i++)
    {
        if (args[i] != "--carrier" || i + 1 >= args.Length)
        {
            Console.WriteLine(PackText.Get("UnrecognizedArgument", args[i]));
            return 2;
        }

        carrier = args[++i];
    }

    if (carrier is not null && !Directory.Exists(carrier))
    {
        Console.WriteLine(PackText.Get("CarrierMissing", carrier));
        return 2;
    }

    return PackLoader(args[1], args[2], args[3], carrier);
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

    return PackLoader(directory, args[2], args[3], null);
}

// --shared 可以重复，写的是**目标载体**的共享层清单 —— 它由载体声明，
// 与运行期 AssemblyPluginLoader 拿到的那份必须同源（ADR-0021 / 0026）。
if (args.Length < 3)
{
    PrintUsage();
    return 2;
}

static void PrintUsage()
{
    Console.WriteLine(PackText.Get("Usage"));
    Console.WriteLine(PackText.Get("UsagePackLoader"));
    Console.WriteLine(PackText.Get("UsagePackPlugin"));
    Console.WriteLine(PackText.Get("UsagePackAssembly"));
    Console.WriteLine(PackText.Get("UsageMigrate"));
    Console.WriteLine(PackText.Get("UsageCarrier"));
    Console.WriteLine(PackText.Get("UsageShared"));
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

// Loader 包的装配：先按共享层清单剔除副本，再交给 Packager 做常规校验与复制。
// 共享清单 = 契约的基地清单 + 目标载体声明的那份（ADR-0061）；载体包用 --carrier 指定。
static int PackLoader(string sourceDirectory, string manifestPath, string outputRoot, string? carrierDirectory)
{
    var shared = carrierDirectory is null
        ? AssemblyPluginLoader.SharedAssemblyFileNames
        : AssemblyPluginLoader.SharedLayerFor(carrierDirectory);
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
