using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Watchpoint.Analyzers;

/// <summary>
/// WP0003: <c>DateTime.Now/UtcNow</c>, <c>DateTimeOffset.Now/UtcNow</c> and <c>Stopwatch.StartNew</c> are forbidden in
/// production code (projects under <c>src/</c>); time is read through an injected <c>TimeProvider</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WallClockAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rules.WallClock);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (Rules.IsTestCode(start.Options))
            {
                return;
            }

            var dateTime = start.Compilation.GetSpecialType(SpecialType.System_DateTime);
            var dateTimeOffset = start.Compilation.GetTypeByMetadataName("System.DateTimeOffset");
            var stopwatch = start.Compilation.GetTypeByMetadataName("System.Diagnostics.Stopwatch");

            start.RegisterOperationAction(
                op =>
                {
                    var property = ((IPropertyReferenceOperation)op.Operation).Property;
                    if (property.IsStatic
                        && property.Name is "Now" or "UtcNow"
                        && (SymbolEqualityComparer.Default.Equals(property.ContainingType, dateTime)
                            || SymbolEqualityComparer.Default.Equals(property.ContainingType, dateTimeOffset)))
                    {
                        op.ReportDiagnostic(Diagnostic.Create(Rules.WallClock, op.Operation.Syntax.GetLocation(), $"{property.ContainingType.Name}.{property.Name}"));
                    }
                },
                OperationKind.PropertyReference);

            start.RegisterOperationAction(
                op =>
                {
                    var method = ((IInvocationOperation)op.Operation).TargetMethod;
                    if (method.Name == "StartNew" && SymbolEqualityComparer.Default.Equals(method.ContainingType, stopwatch))
                    {
                        op.ReportDiagnostic(Diagnostic.Create(Rules.WallClock, op.Operation.Syntax.GetLocation(), "Stopwatch.StartNew"));
                    }
                },
                OperationKind.Invocation);
        });
    }
}
