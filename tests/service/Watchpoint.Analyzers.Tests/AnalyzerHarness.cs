using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Shouldly;

namespace Watchpoint.Analyzers.Tests;

/// <summary>
/// Minimal analyzer test harness: compiles source against the running runtime's assemblies (offline, deterministic),
/// runs one analyzer and compares diagnostics with the spans marked <c>[|…|]</c> in the source.
/// </summary>
internal static class AnalyzerHarness
{
    /// <summary>Stubs of the Kernel result types and xUnit attributes, so tests do not depend on real assemblies.</summary>
    public const string Stubs = """
        namespace Watchpoint.Kernel.Results
        {
            public readonly struct Result { public static Result Success() => new Result(1); private Result(int x) { } }
            public readonly struct Result<T> { public Result(T value) { } }
        }
        namespace Xunit
        {
            public class FactAttribute : System.Attribute { }
            public class TheoryAttribute : FactAttribute { }
        }
        """;

    private static readonly ImmutableArray<MetadataReference> _references =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .Select(p => MetadataReference.CreateFromFile(p)),
    ];

    public static async Task VerifyAsync(DiagnosticAnalyzer analyzer, [StringSyntax("C#")] string markedSource, bool testCode = false)
    {
        var (source, expected) = ParseMarkers(markedSource);
        var diagnostics = await GetDiagnosticsAsync(analyzer, source, testCode);
        var actual = diagnostics.Select(d => d.Location.SourceSpan).Order().ToList();
        actual.ShouldBe(expected, $"diagnostics: {string.Join("; ", diagnostics.Select(d => d.ToString()))}");
    }

    public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(DiagnosticAnalyzer analyzer, string source, bool testCode)
    {
        var compilation = CreateCompilation(source);
        var options = new AnalyzerOptions([], new GlobalOptionsProvider(testCode));
        return await compilation.WithAnalyzers([analyzer], options).GetAnalyzerDiagnosticsAsync(CancellationToken.None);
    }

    public static async Task<string> ApplyFixAsync(DiagnosticAnalyzer analyzer, CodeFixProvider fix, [StringSyntax("C#")] string source)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Test", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithMetadataReferences(_references);
        var document = project.AddDocument("Test.cs", source);
        var compilation = (await document.Project.GetCompilationAsync())!;
        AssertNoErrors(compilation);
        var options = new AnalyzerOptions([], new GlobalOptionsProvider(testCode: false));
        var diagnostics = await compilation.WithAnalyzers([analyzer], options).GetAnalyzerDiagnosticsAsync(CancellationToken.None);
        foreach (var diagnostic in diagnostics.OrderByDescending(d => d.Location.SourceSpan.Start))
        {
            var actions = new List<CodeAction>();
            var context = new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a), CancellationToken.None);
            await fix.RegisterCodeFixesAsync(context);
            var operation = (await actions.ShouldHaveSingleItem().GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
            document = operation.ChangedSolution.GetDocument(document.Id)!;
        }

        AssertNoErrors((await document.Project.GetCompilationAsync())!);
        return (await document.GetTextAsync()).ToString();
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var compilation = CSharpCompilation.Create(
            "Test",
            [CSharpSyntaxTree.ParseText(source, path: "Test.cs"), CSharpSyntaxTree.ParseText(Stubs, path: "Stubs.cs")],
            _references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        AssertNoErrors(compilation);
        return compilation;
    }

    private static void AssertNoErrors(Compilation compilation)
    {
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        errors.ShouldBeEmpty(string.Join(Environment.NewLine, errors));
    }

    private static (string Source, List<TextSpan> Spans) ParseMarkers(string marked)
    {
        var builder = new System.Text.StringBuilder();
        var spans = new List<TextSpan>();
        var starts = new Stack<int>();
        for (var i = 0; i < marked.Length; i++)
        {
            if (string.CompareOrdinal(marked, i, "[|", 0, 2) == 0)
            {
                starts.Push(builder.Length);
                i++;
            }
            else if (string.CompareOrdinal(marked, i, "|]", 0, 2) == 0)
            {
                var start = starts.Pop();
                spans.Add(TextSpan.FromBounds(start, builder.Length));
                i++;
            }
            else
            {
                builder.Append(marked[i]);
            }
        }

        return (builder.ToString(), spans.Order().ToList());
    }

    private sealed class GlobalOptionsProvider(bool testCode) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(testCode ? "true" : null);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }

    private sealed class Options(string? testCode) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value)
        {
            value = key == Rules.TestCodeProperty ? testCode : null;
            return value is not null;
        }
    }
}
