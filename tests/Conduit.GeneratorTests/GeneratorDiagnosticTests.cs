using Conduit.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Conduit.GeneratorTests;

public class GeneratorDiagnosticTests
{
    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using Conduit.Abstractions;
        using Conduit.Contracts;

        """;

    [Test]
    public async Task Duplicate_handlers_are_reported_as_CDI002()
    {
        var result = Run(Usings + """
            public sealed record Ambiguous : IQuery<int>;

            public sealed class First : IRequestHandler<Ambiguous, int>
            {
                public ValueTask<int> HandleAsync(Ambiguous request, CancellationToken cancellationToken)
                    => ValueTask.FromResult(1);
            }

            public sealed class Second : IRequestHandler<Ambiguous, int>
            {
                public ValueTask<int> HandleAsync(Ambiguous request, CancellationToken cancellationToken)
                    => ValueTask.FromResult(2);
            }
            """);

        var duplicate = result.Diagnostics.Where(d => d.Id == "CDI002").ToArray();

        await Assert.That(duplicate.Length).IsEqualTo(1);
        await Assert.That(duplicate[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task Request_without_handler_is_reported_as_CDI001()
    {
        var result = Run(Usings + """
            public sealed record Orphan : IQuery<string>;
            """);

        var missing = result.Diagnostics.Where(d => d.Id == "CDI001").ToArray();

        await Assert.That(missing.Length).IsEqualTo(1);
        await Assert.That(missing[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    }

    [Test]
    public async Task Generated_code_compiles_without_errors()
    {
        var result = Run(Usings + """
            public sealed record Ping : IQuery<string>;

            public sealed class PingHandler : IRequestHandler<Ping, string>
            {
                public ValueTask<string> HandleAsync(Ping request, CancellationToken cancellationToken)
                    => ValueTask.FromResult("pong");
            }
            """);

        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();

        await Assert.That(errors).IsEmpty();
        await Assert.That(result.GeneratedSources.Any(name => name.Contains("Conduit.Generated", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Generated_source_contains_dispatch_entries_for_both_handler_shapes()
    {
        var result = Run(Usings + """
            public sealed record Ping : IQuery<string>;

            public sealed class PingHandler : IRequestHandler<Ping, string>
            {
                public ValueTask<string> HandleAsync(Ping request, CancellationToken cancellationToken)
                    => ValueTask.FromResult("pong");
            }

            public sealed record Numbers : IStreamRequest<int>;

            public sealed class NumbersHandler : IStreamRequestHandler<Numbers, int>
            {
                public async IAsyncEnumerable<int> HandleStreamAsync(
                    Numbers request,
                    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
                {
                    await Task.Yield();
                    yield return 1;
                }
            }
            """);

        var source = result.GeneratedSourceText;

        await Assert.That(source).Contains("RequestHandlerEntry<global::Ping, string>");
        await Assert.That(source).Contains("StreamHandlerEntry<global::Numbers, int>");
    }

    private static RunnerResult Run(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.None),
            path: "Input.cs");

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(static path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create(
            "GeneratorTests.Input",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new ConduitGenerator().AsSourceGenerator()],
            parseOptions: (CSharpParseOptions)syntaxTree.Options);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        // 注意：RunGeneratorsAndUpdateCompilation 的 out 诊断与 runResult.Diagnostics 内容重合，
        // 只取后者，避免同一条诊断被计两次。
        var runResult = driver.GetRunResult();

        var generated = runResult.GeneratedTrees
            .Select(tree => (tree.FilePath, tree.GetText().ToString()))
            .ToArray();

        return new RunnerResult(
            [.. runResult.Diagnostics],
            [.. outputCompilation.GetDiagnostics()],
            generated.Select(static g => g.FilePath).ToArray(),
            generated.Length == 0 ? string.Empty : generated[0].Item2);
    }

    private sealed record RunnerResult(
        Diagnostic[] Diagnostics,
        Diagnostic[] CompilationDiagnostics,
        string[] GeneratedSources,
        string GeneratedSourceText);
}
