using System.Reflection;
using HandlerR.Contracts;

namespace HandlerR.Tests;

/// <summary>
/// 架构合规测试：把技术方案的独立性红线与工程约束钉成可执行断言。
/// </summary>
public class ArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private static string SourceRoot => Path.Combine(RepositoryRoot, "src");

    [Test]
    public async Task No_source_file_mentions_the_reference_implementation()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SourceRoot, "*.*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (extension is not (".cs" or ".csproj") || IsInBuildOutput(file))
            {
                continue;
            }

            var text = await File.ReadAllTextAsync(file);
            if (text.Contains("MediatR", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(RepositoryRoot, file));
            }
        }

        await Assert.That(offenders).IsEmpty();
    }

    [Test]
    public async Task Contracts_has_no_package_dependencies()
    {
        var project = Path.Combine(SourceRoot, "HandlerR.Contracts", "HandlerR.Contracts.csproj");
        var text = await File.ReadAllTextAsync(project);

        await Assert.That(text.Contains("PackageReference", StringComparison.Ordinal)).IsFalse();
        await Assert.That(text.Contains("ProjectReference", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task Contracts_exposes_exactly_six_public_types()
    {
        var exported = typeof(Unit).Assembly.GetExportedTypes();

        await Assert.That(exported.Length).IsEqualTo(6);
        await Assert.That(exported.Select(static t => t.Name)).Contains(nameof(Unit));
    }

    [Test]
    public async Task Abstractions_depends_only_on_contracts()
    {
        var project = Path.Combine(SourceRoot, "HandlerR.Abstractions", "HandlerR.Abstractions.csproj");
        var text = await File.ReadAllTextAsync(project);

        await Assert.That(text.Contains("PackageReference", StringComparison.Ordinal)).IsFalse();
        await Assert.That(text.Contains("HandlerR.Contracts.csproj", StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.Contains("HandlerR.Core.csproj", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task Core_does_not_reference_dependency_injection_abstractions()
    {
        var project = Path.Combine(SourceRoot, "HandlerR.Core", "HandlerR.Core.csproj");
        var text = StripXmlComments(await File.ReadAllTextAsync(project));

        await Assert.That(text.Contains("PackageReference", StringComparison.Ordinal)).IsFalse();
        await Assert.That(text.Contains("Microsoft.Extensions", StringComparison.Ordinal)).IsFalse();
        await Assert.That(text.Contains("HandlerR.Abstractions.csproj", StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.Contains("HandlerR.DependencyInjection.csproj", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>剥掉 XML 注释，避免注释里的说明文字被当成实际引用。</summary>
    private static string StripXmlComments(string text)
    {
        while (true)
        {
            var start = text.IndexOf("<!--", StringComparison.Ordinal);
            if (start < 0)
            {
                return text;
            }

            var end = text.IndexOf("-->", start, StringComparison.Ordinal);
            if (end < 0)
            {
                return text;
            }

            text = string.Concat(text.AsSpan(0, start), text.AsSpan(end + 3));
        }
    }

    [Test]
    public async Task Every_product_project_targets_net10_only()
    {
        var props = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot, "Directory.Build.props"));
        await Assert.That(props).Contains("<TargetFramework>net10.0</TargetFramework>");

        var offenders = new List<string>();
        string[] legacyTargets = ["netstandard", "net462", "net472", "net48", "net8.0", "net9.0", "net11.0"];

        foreach (var project in Directory.EnumerateFiles(SourceRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(project);

            // 源生成器受 Roslyn 分析器宿主约束，是唯一豁免项
            if (name == "HandlerR.SourceGenerator")
            {
                continue;
            }

            // 只看真正的 TargetFramework(s) 元素：注释与 MSBuild 任务参数里出现 netstandard2.0
            // 属于合法用法（例如把源生成器作为 analyzers 资产收集），不应判为违规。
            var declared = System.Xml.Linq.XDocument.Load(project)
                .Descendants()
                .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                .Select(element => element.Value.Trim())
                .ToArray();

            // 未声明即继承 Directory.Build.props 的 net10.0
            if (declared.Length == 0)
            {
                continue;
            }

            var multiTarget = declared.Any(value => value.Contains(';'));
            var legacy = declared.Any(value => legacyTargets.Any(
                target => value.Contains(target, StringComparison.OrdinalIgnoreCase)));

            if (multiTarget || legacy)
            {
                offenders.Add($"{name} => {string.Join(" | ", declared)}");
            }
        }

        await Assert.That(string.Join("; ", offenders)).IsEmpty();
    }

    [Test]
    public async Task Source_generator_project_stays_on_netstandard2()
    {
        var project = Path.Combine(SourceRoot, "HandlerR.SourceGenerator", "HandlerR.SourceGenerator.csproj");
        var text = await File.ReadAllTextAsync(project);

        await Assert.That(text).Contains("netstandard2.0");
    }

    [Test]
    public async Task No_polyfill_package_is_pinned()
    {
        var props = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot, "Directory.Packages.props"));

        foreach (var forbidden in new[]
                 {
                     "System.Threading.Tasks.Extensions",
                     "Microsoft.Bcl.AsyncInterfaces",
                     "System.Diagnostics.DiagnosticSource",
                     "System.Collections.Immutable",
                     "Microsoft.NET.Test.Sdk",
                     "xunit",
                     "FluentAssertions",
                     "NSubstitute",
                 })
        {
            await Assert.That(props.Contains($"Include=\"{forbidden}\"", StringComparison.Ordinal)).IsFalse();
        }
    }

    [Test]
    public async Task Notification_publishers_are_replaceable_and_cover_three_strategies()
    {
        var publishers = typeof(HandlerR.Core.Publishing.SequentialPublisher).Assembly
            .GetExportedTypes()
            .Where(static t => typeof(HandlerR.Abstractions.INotificationPublisher).IsAssignableFrom(t))
            .Select(static t => t.Name)
            .ToArray();

        await Assert.That(publishers).Contains(nameof(HandlerR.Core.Publishing.SequentialPublisher));
        await Assert.That(publishers).Contains(nameof(HandlerR.Core.Publishing.ParallelPublisher));
        await Assert.That(publishers).Contains(nameof(HandlerR.Core.Publishing.AggregatingParallelPublisher));
    }

    [Test]
    public async Task Contracts_types_are_defined_only_in_the_contracts_assembly()
    {
        var contractTypeNames = new[]
        {
            nameof(IBaseRequest), nameof(IRequest), "IRequest`1", nameof(INotification), "IStreamRequest`1", nameof(Unit),
        };

        foreach (var assembly in new[]
                 {
                     typeof(HandlerR.Abstractions.IDispatcher).Assembly,
                     typeof(HandlerR.Core.HandlerRDispatcher).Assembly,
                     typeof(HandlerR.DependencyInjection.HandlerRConfiguration).Assembly,
                 })
        {
            var duplicates = assembly.GetExportedTypes()
                .Where(t => t.Namespace == "HandlerR.Contracts")
                .Select(static t => t.Name)
                .ToArray();

            await Assert.That(duplicates).IsEmpty();
        }

        await Assert.That(contractTypeNames.Length).IsEqualTo(6);
    }

    private static bool IsInBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
