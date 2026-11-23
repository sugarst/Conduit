#:property EnableNETAnalyzers=false
#:property GenerateDocumentationFile=false

// HandlerR 合规门禁（.NET 10 file-based app）。
//
// 覆盖技术方案第 1 / 3 / 11 节的红线与工程约束：
//   · 红线 #1：产品源码与依赖图不得出现对标库（含传递依赖）
//   · 契约包 HandlerR.Contracts 依赖列表必须为空
//   · 产品依赖图禁止 polyfill 包
//   · 测试栈统一 TUnit / Microsoft.Testing.Platform，禁用包黑名单
//   · TFM 合规：除源生成器（Roslyn 宿主约束）外只允许 net10.0
//   · --packages：校验 nupkg 依赖声明与 lib / analyzers 资产布局
//
// 用法（先还原项目依赖）:
//   dotnet run scripts/check_compliance.cs [-- --packages] [--root <repo>]
//
// 退出码: 0 = 通过, 1 = 违规, 2 = 运行环境异常

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

var exitCode = ComplianceRunner.Run(args);
return exitCode;

internal static class ComplianceRunner
{
    private static readonly string[] ScannedRoots = ["src", "samples", "benchmarks", "tests"];

    private static readonly string[] ScannedExtensions = [".cs", ".csproj", ".props"];

    private static readonly string[] PolyfillPackages =
    [
        "System.Threading.Tasks.Extensions",
        "Microsoft.Bcl.AsyncInterfaces",
        "System.Diagnostics.DiagnosticSource",
        "System.Collections.Immutable",
    ];

    private static readonly string[] ForbiddenTestPackages =
    [
        "Microsoft.NET.Test.Sdk",
        "xunit.runner.visualstudio",
        "FluentAssertions",
        "NSubstitute",
        "MSTest.TestFramework",
    ];

    private static readonly string[] LegacyTargetMarkers =
    [
        "netstandard", "net46", "net47", "net48", "net8.0", "net9.0", "net11.0",
    ];

    private static readonly string[] InternalAssemblyIds =
    [
        "HandlerR.Abstractions", "HandlerR.Core", "HandlerR.DependencyInjection",
    ];

    private const string SourceGeneratorProject = "HandlerR.SourceGenerator.csproj";

    private static readonly List<string> Violations = [];
    private static string root = Directory.GetCurrentDirectory();

    public static int Run(string[] args)
    {
        var (checkPackages, rootOption, help) = ParseArgs(args);

        if (help)
        {
            PrintUsage();
            return 0;
        }

        root = rootOption is null ? FindRepositoryRoot() : Path.GetFullPath(rootOption);

        if (!Directory.Exists(Path.Combine(root, "src")))
        {
            Console.Error.WriteLine($"[compliance] 不是仓库根目录（缺少 src/）：{root}");
            return 2;
        }

        Dictionary<string, HashSet<string>> productDependencies;
        Dictionary<string, HashSet<string>> testDependencies;
        try
        {
            productDependencies = CollectDependencies(EnumerateProjects("src", excludeSourceGenerator: true));
            testDependencies = CollectDependencies(EnumerateProjects("tests", excludeSourceGenerator: false));
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"[compliance] 无法读取 NuGet 依赖图。{exception.Message}");
            return 2;
        }

        Section("红线 #1：不得引用对标库（含传递依赖）");
        CheckReferenceLibraryTraces();

        CheckProductDependencyGraph(productDependencies);

        Section("契约包零依赖");
        CheckContractsHasNoDependency(productDependencies);

        Section("禁止 polyfill 包（仅产品运行时项目）");
        CheckNoPolyfill(productDependencies);

        Section("测试栈红线（仅测试项目）");
        CheckTestStack(testDependencies);

        Section("TFM 合规：除源生成器外只允许 net10.0");
        CheckTargetFrameworks();

        if (checkPackages)
        {
            Section("NuGet 包结构");
            CheckPackageLayout();
        }

        Console.WriteLine();
        if (Violations.Count > 0)
        {
            Console.WriteLine($"[compliance] FAILED：{Violations.Count} 项违规");
            foreach (var violation in Violations)
            {
                Console.Error.WriteLine($"  - {violation}");
            }

            return 1;
        }

        Console.WriteLine("[compliance] PASSED");
        return 0;
    }

    // ---------------------------------------------------------------- 检查项

    private static void CheckReferenceLibraryTraces()
    {
        var hits = new List<string>();

        foreach (var directory in ScannedRoots)
        {
            var path = Path.Combine(root, directory);
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file)
                    || !ScannedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                    // 架构合规测试自身以字符串断言"无对标库痕迹"，是唯一豁免
                    || file.EndsWith("ArchitectureTests.cs", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    if (File.ReadAllText(file).Contains("MediatR", StringComparison.Ordinal))
                    {
                        hits.Add(Path.GetRelativePath(root, file));
                    }
                }
                catch (IOException)
                {
                    // 文件被占用时跳过，不影响整体判定
                }
            }
        }

        Report(hits.Count == 0, "源码无对标库痕迹（架构断言文件除外）", $"源码出现对标库痕迹：{string.Join(", ", hits)}");
    }

    private static void CheckProductDependencyGraph(Dictionary<string, HashSet<string>> productDependencies)
    {
        var mediatrHits = productDependencies
            .Where(pair => pair.Value.Contains("MediatR"))
            .Select(pair => pair.Key)
            .ToArray();

        Report(
            mediatrHits.Length == 0,
            "产品依赖图无对标库包",
            $"产品依赖图出现对标库包：{string.Join(", ", mediatrHits)}");
    }

    private static void CheckContractsHasNoDependency(Dictionary<string, HashSet<string>> productDependencies)
    {
        var contracts = productDependencies
            .FirstOrDefault(pair => Path.GetFileName(pair.Key) == "HandlerR.Contracts.csproj");

        if (contracts.Key is null)
        {
            Report(false, string.Empty, "未找到 HandlerR.Contracts 项目");
            return;
        }

        Report(
            contracts.Value.Count == 0,
            "HandlerR.Contracts 依赖列表为空",
            "HandlerR.Contracts 存在包依赖");
    }

    private static void CheckNoPolyfill(Dictionary<string, HashSet<string>> productDependencies)
    {
        var offenders = new List<string>();

        foreach (var package in PolyfillPackages)
        {
            foreach (var (project, dependencies) in productDependencies)
            {
                if (dependencies.Contains(package))
                {
                    offenders.Add($"{Path.GetFileName(project)} -> {package}");
                }
            }
        }

        Report(offenders.Count == 0, "产品依赖图无 polyfill 包", $"产品依赖图出现 polyfill 包：{string.Join(", ", offenders)}");
    }

    private static void CheckTestStack(Dictionary<string, HashSet<string>> testDependencies)
    {
        var offenders = new List<string>();

        foreach (var package in ForbiddenTestPackages)
        {
            foreach (var (project, dependencies) in testDependencies)
            {
                if (dependencies.Contains(package))
                {
                    offenders.Add($"{Path.GetFileName(project)} -> {package}");
                }
            }
        }

        Report(offenders.Count == 0, "测试项目无禁用包", $"测试项目出现禁用包：{string.Join(", ", offenders)}");

        var usesTunit = testDependencies.Values.Any(dependencies => dependencies.Contains("TUnit"));
        Report(usesTunit, "测试栈为 TUnit / Microsoft.Testing.Platform", "未检测到 TUnit");
    }

    private static void CheckTargetFrameworks()
    {
        var offenders = new List<string>();

        foreach (var project in EnumerateProjects("src", excludeSourceGenerator: true))
        {
            XDocument document;
            try
            {
                document = XDocument.Load(project);
            }
            catch (Exception error) when (error is IOException or System.Xml.XmlException)
            {
                offenders.Add($"{Path.GetFileName(project)} 无法解析：{error.Message}");
                continue;
            }

            // 只看真正的 TargetFramework(s) 元素：注释与 MSBuild 任务参数里出现 netstandard2.0
            // 属合法用法（例如把源生成器作为 analyzers 资产收集），不应判为违规。
            var declared = document.Descendants()
                .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                .Select(element => element.Value.Trim())
                .ToArray();

            if (declared.Length == 0)
            {
                continue;
            }

            if (declared.Any(value => value.Contains(';')))
            {
                offenders.Add($"{Path.GetFileName(project)} 声明了多目标：{string.Join(" | ", declared)}");
                continue;
            }

            foreach (var value in declared)
            {
                if (LegacyTargetMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                {
                    offenders.Add($"{Path.GetFileName(project)} TargetFramework={value}");
                }
            }
        }

        Report(offenders.Count == 0, "所有产品项目单一 net10.0 资产", $"TFM 违规：{string.Join(", ", offenders)}");

        var propsPath = Path.Combine(root, "Directory.Build.props");
        var propsDeclaresNet10 = File.Exists(propsPath)
            && File.ReadAllText(propsPath).Contains("<TargetFramework>net10.0</TargetFramework>", StringComparison.Ordinal);

        Report(propsDeclaresNet10, "Directory.Build.props 统一声明 net10.0", "Directory.Build.props 未统一声明 net10.0");

        var generatorProject = Path.Combine(root, "src", "HandlerR.SourceGenerator", SourceGeneratorProject);
        var generatorStaysOnNetStandard = File.Exists(generatorProject)
            && File.ReadAllText(generatorProject).Contains("<TargetFramework>netstandard2.0</TargetFramework>", StringComparison.Ordinal);

        Report(generatorStaysOnNetStandard, "源生成器保持 netstandard2.0（Roslyn 宿主约束）", "源生成器 TFM 异常");
    }

    private static void CheckPackageLayout()
    {
        var artifacts = Path.Combine(root, "artifacts");
        var handlerrPackage = FindPackage(artifacts, "HandlerR");
        var contractsPackage = FindPackage(artifacts, "HandlerR.Contracts");

        if (handlerrPackage is null || contractsPackage is null)
        {
            Report(false, string.Empty, "artifacts/ 缺少 nupkg，请先执行 pack");
            return;
        }

        using var handlerrArchive = ZipFile.OpenRead(handlerrPackage);
        var handlerrNuspec = ReadNuspec(handlerrArchive, "HandlerR.nuspec");
        var handlerrEntries = handlerrArchive.Entries.Select(entry => entry.FullName).ToArray();

        var leaksInternals = InternalAssemblyIds.Any(id =>
            handlerrNuspec.Contains($"dependency id=\"{id}\"", StringComparison.Ordinal));

        Report(!leaksInternals, "主包不暴露内部程序集依赖", "主包把内部程序集声明为 NuGet 依赖");

        var pinsContractsRange = handlerrNuspec.Contains(
            "dependency id=\"HandlerR.Contracts\" version=\"[1.0.0, 2.0.0)\"",
            StringComparison.Ordinal);

        Report(pinsContractsRange, "主包以 [1.0.0, 2.0.0) 区间依赖契约包", "主包未按区间依赖契约包");

        var libAssemblies = handlerrEntries.Count(entry =>
            entry.StartsWith("lib/net10.0/", StringComparison.Ordinal)
            && entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

        Report(
            libAssemblies == 3,
            "主包 lib/net10.0 仅含 Abstractions/Core/DI 三个程序集",
            $"主包 lib/net10.0 程序集数量为 {libAssemblies}（期望 3）");

        var hasGenerator = handlerrEntries.Any(entry =>
            entry.Equals("analyzers/dotnet/cs/HandlerR.SourceGenerator.dll", StringComparison.Ordinal));

        Report(hasGenerator, "主包内含源生成器 analyzers 资产", "主包缺少源生成器 analyzers 资产");

        using var contractsArchive = ZipFile.OpenRead(contractsPackage);
        var contractsNuspec = ReadNuspec(contractsArchive, "HandlerR.Contracts.nuspec");
        Report(
            !contractsNuspec.Contains("<dependency", StringComparison.Ordinal),
            "契约包无任何依赖声明",
            "契约包声明了依赖");
    }

    // ---------------------------------------------------------------- 基础设施

    private static Dictionary<string, HashSet<string>> CollectDependencies(string[] projects)
    {
        var results = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            var relativePath = Path.GetRelativePath(root, project);
            var (exitCode, output, error) = RunDotnet("package", "list", "--project", project,
                "--include-transitive", "--no-restore", "--format", "json", "--output-version", "1");
            if (exitCode != 0)
            {
                var diagnostic = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
                throw new InvalidOperationException(
                    $"{relativePath}（dotnet package list 退出码 {exitCode}）。请确认项目依赖已还原。{Environment.NewLine}{diagnostic}");
            }

            try
            {
                using var report = JsonDocument.Parse(output);
                var frameworks = report.RootElement.GetProperty("projects").EnumerateArray().Single().GetProperty("frameworks");
                if (frameworks.GetArrayLength() == 0)
                {
                    throw new JsonException("报告中没有目标框架。");
                }

                var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var framework in frameworks.EnumerateArray())
                {
                    foreach (var groupName in new[] { "topLevelPackages", "transitivePackages" })
                    {
                        if (framework.TryGetProperty(groupName, out var group))
                        {
                            foreach (var package in group.EnumerateArray())
                            {
                                dependencies.Add(package.GetProperty("id").GetString()
                                    ?? throw new JsonException("依赖缺少包 ID。"));
                            }
                        }
                    }
                }

                results.Add(project, dependencies);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                throw new InvalidOperationException($"{relativePath} 的 NuGet JSON 报告无效。{exception.Message}", exception);
            }
        }

        return results;
    }

    private static string[] EnumerateProjects(string directory, bool excludeSourceGenerator)
    {
        var path = Path.Combine(root, directory);
        if (!Directory.Exists(path))
        {
            return [];
        }

        return Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .Where(file => !excludeSourceGenerator || Path.GetFileName(file) != SourceGeneratorProject)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsBuildOutput(string path)
    {
        var separator = Path.DirectorySeparatorChar;
        return path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
            || path.Contains($"{separator}obj{separator}", StringComparison.Ordinal);
    }

    private static (int ExitCode, string Output, string Error) RunDotnet(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };

        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 dotnet 进程");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return (process.ExitCode, outputTask.GetAwaiter().GetResult(), errorTask.GetAwaiter().GetResult());
    }

    private static string? FindPackage(string artifacts, string packageId)
    {
        if (!Directory.Exists(artifacts))
        {
            return null;
        }

        // 注意：.NET 的文件通配只支持 * 与 ?，不支持 [0-9] 字符类，这里用正则区分
        // "HandlerR.1.0.0.nupkg" 与 "HandlerR.Contracts.1.0.0.nupkg"
        var versionedName = new System.Text.RegularExpressions.Regex(
            $@"^{System.Text.RegularExpressions.Regex.Escape(packageId)}\.\d",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return Directory.EnumerateFiles(artifacts, "*.nupkg")
            .Where(file => !Path.GetFileName(file).Contains(".symbols.", StringComparison.Ordinal))
            .Where(file => versionedName.IsMatch(Path.GetFileName(file)))
            .OrderBy(file => file, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string ReadNuspec(ZipArchive archive, string expectedEntryName)
    {
        var entry = archive.Entries.FirstOrDefault(candidate =>
            candidate.FullName.Equals(expectedEntryName, StringComparison.Ordinal))
            ?? archive.Entries.FirstOrDefault(candidate =>
                candidate.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return string.Empty;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static (bool CheckPackages, string? RootOption, bool Help) ParseArgs(string[] args)
    {
        var checkPackages = false;
        string? rootOption = null;
        var help = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--packages":
                    checkPackages = true;
                    break;
                case "--root" when index + 1 < args.Length:
                    rootOption = args[++index];
                    break;
                case "--help" or "-h":
                    help = true;
                    break;
                default:
                    break;
            }
        }

        return (checkPackages, rootOption, help);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HandlerR.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title} ===");
    }

    private static void Report(bool passed, string successMessage, string failureMessage)
    {
        if (passed)
        {
            Console.WriteLine($"  [ok]   {successMessage}");
            return;
        }

        Console.WriteLine($"  [FAIL] {failureMessage}");
        Violations.Add(failureMessage);
    }

    private static void PrintUsage() => Console.WriteLine(
        """
        用法: dotnet run scripts/check_compliance.cs [-- --packages] [--root <repo>]

          --packages    追加校验已打包 nupkg 的依赖声明与资产布局（需先 pack）
          --root <path> 仓库根目录，默认从当前目录向上查找 HandlerR.slnx
          --help, -h    显示本帮助

        依赖检查读取已还原的项目图。请先还原或构建项目。
        退出码: 0 通过 / 1 违规 / 2 运行环境异常
        """);
}
