using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Watchpoint.Analyzers;

/// <summary>WP0002: a <c>[Fact]</c>/<c>[Theory]</c> method name must start with a test id (<c>REG_U_012_…</c>).</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TestNamingAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Test id prefix: spec id segments (e.g. <c>REG</c>, <c>X_API_REG</c>), layer letter, three digits.</summary>
    public static readonly Regex TestIdPrefix = new(@"^[A-Z][A-Z0-9]*(?:_[A-Z][A-Z0-9]*)*_[UICEPS]_\d{3}_", RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rules.TestNaming);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var fact = start.Compilation.GetTypeByMetadataName("Xunit.FactAttribute");
            if (fact is null)
            {
                return;
            }

            start.RegisterSymbolAction(
                symbolContext =>
                {
                    var method = (IMethodSymbol)symbolContext.Symbol;
                    if (method.GetAttributes().Any(a => InheritsFrom(a.AttributeClass, fact)) && !TestIdPrefix.IsMatch(method.Name))
                    {
                        symbolContext.ReportDiagnostic(Diagnostic.Create(Rules.TestNaming, method.Locations[0], method.Name));
                    }
                },
                SymbolKind.Method);
        });
    }

    private static bool InheritsFrom(INamedTypeSymbol? type, INamedTypeSymbol baseType)
    {
        for (; type is not null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type, baseType))
            {
                return true;
            }
        }

        return false;
    }
}
