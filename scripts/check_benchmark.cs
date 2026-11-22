#:property EnableNETAnalyzers=false
#:property GenerateDocumentationFile=false

// Conduit 基准回归门禁（.NET 10 file-based app）。
//
// 读取 BenchmarkDotNet 导出的 JSON 报告，与 benchmarks/baseline.json 比较：
// 均值或分配量超出基线 stopLossMultiplier 倍即判定回归（技术方案第 13 节止损条件）。
//
// 用法:
//   dotnet run scripts/check_benchmark.cs -- --report <bdn-full-json> [--baseline <path>] [--root <repo>]
//
// 退出码: 0 = 通过, 1 = 回归超阈值, 2 = 输入不可用

using System.Globalization;
using System.Text.Json;

var exitCode = Runner.Run(args);
return exitCode;

internal sealed record Options(string? Report, string? Baseline, string? Root, bool Help);

internal sealed record Entry(double MeanNs, double AllocatedBytes);

internal sealed record Reference(string Role, double MeanNs, double AllocatedBytes);

internal static class Runner
{
    public static int Run(string[] args)
    {
        var options = ParseArgs(args);
        if (options.Help)
        {
            PrintUsage();
            return 0;
        }

        if (options.Report is null)
        {
            Console.Error.WriteLine("[benchmark] 缺少 --report <BenchmarkDotNet JSON 报告路径>");
            PrintUsage();
            return 2;
        }

        var reportPath = Path.GetFullPath(options.Report);
        if (!File.Exists(reportPath))
        {
            Console.Error.WriteLine($"[benchmark] report not found: {reportPath}");
            return 2;
        }

        var root = options.Root is null ? FindRepositoryRoot() : Path.GetFullPath(options.Root);
        var baselinePath = options.Baseline is null
            ? Path.Combine(root, "benchmarks", "baseline.json")
            : Path.GetFullPath(options.Baseline);

        if (!File.Exists(baselinePath))
        {
            Console.Error.WriteLine($"[benchmark] baseline not found: {baselinePath}");
            return 2;
        }

        Dictionary<string, Reference> expected;
        double multiplier;
        try
        {
            using var baselineDocument = JsonDocument.Parse(File.ReadAllText(baselinePath));
            (expected, multiplier) = ReadBaseline(baselineDocument.RootElement);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Console.Error.WriteLine($"[benchmark] baseline 解析失败: {error.Message}");
            return 2;
        }

        Dictionary<string, Entry> measured;
        try
        {
            using var reportDocument = JsonDocument.Parse(File.ReadAllText(reportPath));
            measured = ReadReport(reportDocument.RootElement);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"[benchmark] report 解析失败: {error.Message}");
            return 2;
        }

        return Compare(expected, measured, multiplier);
    }

    private static int Compare(Dictionary<string, Reference> expected, Dictionary<string, Entry> measured, double multiplier)
    {
        var failures = new List<string>();

        Console.WriteLine($"[benchmark] stop-loss multiplier = {multiplier.ToString("0.0#", CultureInfo.InvariantCulture)}x");
        Console.WriteLine(
            Pad("benchmark", 26) + PadLeft("mean ns", 12) + PadLeft("limit ns", 12)
            + PadLeft("alloc B", 10) + PadLeft("limit B", 10) + "  status");

        foreach (var (name, reference) in expected)
        {
            var meanLimit = reference.MeanNs * multiplier;
            var allocLimit = Math.Max(reference.AllocatedBytes * multiplier, reference.AllocatedBytes + 64);

            if (!measured.TryGetValue(name, out var current))
            {
                failures.Add($"{name}: missing from report");
                Console.WriteLine(
                    Pad(name, 26) + PadLeft("-", 12) + PadLeft("-", 12) + PadLeft("-", 10) + PadLeft("-", 10)
                    + "  MISSING");
                continue;
            }

            var status = "ok";
            if (current.MeanNs > meanLimit)
            {
                status = "REGRESSED";
                failures.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: mean {1:0.0} ns > limit {2:0.0} ns",
                    name,
                    current.MeanNs,
                    meanLimit));
            }
            else if (current.AllocatedBytes > allocLimit)
            {
                status = "REGRESSED";
                failures.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: allocated {1:0} B > limit {2:0} B",
                    name,
                    current.AllocatedBytes,
                    allocLimit));
            }

            Console.WriteLine(
                Pad(name, 26)
                + PadLeft(current.MeanNs.ToString("0.0", CultureInfo.InvariantCulture), 12)
                + PadLeft(meanLimit.ToString("0.0", CultureInfo.InvariantCulture), 12)
                + PadLeft(current.AllocatedBytes.ToString("0", CultureInfo.InvariantCulture), 10)
                + PadLeft(allocLimit.ToString("0", CultureInfo.InvariantCulture), 10)
                + "  " + status);
        }

        if (failures.Count > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("[benchmark] FAILED");
            foreach (var failure in failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }

            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("[benchmark] PASSED");
        return 0;
    }

    private static (Dictionary<string, Reference> Expected, double Multiplier) ReadBaseline(JsonElement root)
    {
        var multiplier = root.GetProperty("acceptance").TryGetProperty("stopLossMultiplier", out var multiplierElement)
            ? multiplierElement.GetDouble()
            : 2.0;

        var expected = new Dictionary<string, Reference>(StringComparer.Ordinal);
        foreach (var property in root.GetProperty("benchmarks").EnumerateObject())
        {
            var role = property.Value.TryGetProperty("role", out var roleElement)
                ? roleElement.GetString() ?? string.Empty
                : string.Empty;

            expected[property.Name] = new Reference(
                role,
                property.Value.GetProperty("meanNs").GetDouble(),
                property.Value.TryGetProperty("allocatedBytesPerOperation", out var allocated)
                    ? allocated.GetDouble()
                    : 0.0);
        }

        return (expected, multiplier);
    }

    private static Dictionary<string, Entry> ReadReport(JsonElement root)
    {
        var benchmarks = root.TryGetProperty("Benchmarks", out var upper)
            ? upper
            : root.TryGetProperty("benchmarks", out var lower)
                ? lower
                : throw new InvalidOperationException("报告缺少 'Benchmarks' 数组");

        var measured = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var entry in benchmarks.EnumerateArray())
        {
            var fullName = entry.TryGetProperty("FullName", out var fullNameElement)
                ? fullNameElement.GetString()
                : entry.TryGetProperty("fullName", out var fullNameLower)
                    ? fullNameLower.GetString()
                    : null;

            if (string.IsNullOrEmpty(fullName))
            {
                continue;
            }

            var shortName = fullName[(fullName.LastIndexOf('.') + 1)..];

            var statistics = entry.TryGetProperty("Statistics", out var upperStatistics)
                ? upperStatistics
                : entry.TryGetProperty("statistics", out var lowerStatistics)
                    ? lowerStatistics
                    : default;

            var mean = statistics.ValueKind == JsonValueKind.Object
                && statistics.TryGetProperty("Mean", out var meanElement)
                    ? meanElement.GetDouble()
                    : 0.0;

            var memory = entry.TryGetProperty("Memory", out var upperMemory)
                ? upperMemory
                : entry.TryGetProperty("memory", out var lowerMemory)
                    ? lowerMemory
                    : default;

            var allocated = 0.0;
            if (memory.ValueKind == JsonValueKind.Object)
            {
                if (memory.TryGetProperty("BytesAllocatedPerOperation", out var bytes))
                {
                    allocated = bytes.GetDouble();
                }
                else if (memory.TryGetProperty("bytesAllocatedPerOperation", out var bytesLower))
                {
                    allocated = bytesLower.GetDouble();
                }
            }

            // 同一方法可能因多个 Job 出现多次，取最小值作为本轮观测
            if (!measured.TryGetValue(shortName, out var previous) || mean < previous.MeanNs)
            {
                measured[shortName] = new Entry(mean, allocated);
            }
        }

        return measured;
    }

    private static Options ParseArgs(string[] args)
    {
        string? report = null;
        string? baseline = null;
        string? root = null;
        var help = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--report" when index + 1 < args.Length:
                    report = args[++index];
                    break;
                case "--baseline" when index + 1 < args.Length:
                    baseline = args[++index];
                    break;
                case "--root" when index + 1 < args.Length:
                    root = args[++index];
                    break;
                case "--help" or "-h":
                    help = true;
                    break;
                default:
                    break;
            }
        }

        return new Options(report, baseline, root, help);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Conduit.slnx"))
                || File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string Pad(string value, int width) => value.PadRight(width);

    private static string PadLeft(string value, int width) => value.PadLeft(width);

    private static void PrintUsage() => Console.WriteLine(
        """
        用法: dotnet run scripts/check_benchmark.cs -- --report <BenchmarkDotNet JSON 报告> [选项]

          --report <path>     必需。BenchmarkDotNet 的 *-report-full-compressed.json
          --baseline <path>   可选。默认 <repo>/benchmarks/baseline.json
          --root <path>       可选。仓库根目录，默认从当前目录向上查找
          --help, -h          显示本帮助

        退出码: 0 通过 / 1 回归超阈值 / 2 输入不可用
        """);
}
