using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Watchpoint.Analyzers;

/// <summary>WP0001: <c>default(Result)</c>, <c>default(Result&lt;T&gt;)</c>, <c>new Result&lt;T&gt;()</c> are not allowed.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DefaultResultAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rules.DefaultResult);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var result = start.Compilation.GetTypeByMetadataName("Watchpoint.Kernel.Results.Result");
            var resultOfT = start.Compilation.GetTypeByMetadataName("Watchpoint.Kernel.Results.Result`1");
            if (result is null && resultOfT is null)
            {
                return;
            }

            bool IsResult(ITypeSymbol? type) =>
                type is INamedTypeSymbol named
                && (SymbolEqualityComparer.Default.Equals(named, result) || SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, resultOfT));

            bool InsideResult(ISymbol symbol)
            {
                for (var type = symbol.ContainingType; type is not null; type = type.ContainingType)
                {
                    if (IsResult(type))
                    {
                        return true;
                    }
                }

                return false;
            }

            start.RegisterOperationAction(
                op =>
                {
                    if (IsResult(op.Operation.Type) && !InsideResult(op.ContainingSymbol))
                    {
                        op.ReportDiagnostic(Diagnostic.Create(Rules.DefaultResult, op.Operation.Syntax.GetLocation(), op.Operation.Syntax.ToString(), op.Operation.Type!.Name));
                    }
                },
                OperationKind.DefaultValue);

            start.RegisterOperationAction(
                op =>
                {
                    var creation = (IObjectCreationOperation)op.Operation;
                    if (IsResult(creation.Type) && creation.Constructor is { Parameters.Length: 0, IsImplicitlyDeclared: true } && !InsideResult(op.ContainingSymbol))
                    {
                        op.ReportDiagnostic(Diagnostic.Create(Rules.DefaultResult, creation.Syntax.GetLocation(), creation.Syntax.ToString(), creation.Type!.Name));
                    }
                },
                OperationKind.ObjectCreation);
        });
    }
}
