using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Watchpoint.Analyzers;

/// <summary>WP0005: <c>Thread.Sleep</c> and <c>Task.Delay</c> without a <c>TimeProvider</c> argument are forbidden in test projects.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SleepInTestsAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rules.SleepInTests);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (!Rules.IsTestCode(start.Options))
            {
                return;
            }

            var thread = start.Compilation.GetTypeByMetadataName("System.Threading.Thread");
            var task = start.Compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
            var timeProvider = start.Compilation.GetTypeByMetadataName("System.TimeProvider");

            start.RegisterOperationAction(
                op =>
                {
                    var method = ((IInvocationOperation)op.Operation).TargetMethod;
                    var forbidden = (method.Name == "Sleep" && SymbolEqualityComparer.Default.Equals(method.ContainingType, thread))
                        || (method.Name == "Delay"
                            && SymbolEqualityComparer.Default.Equals(method.ContainingType, task)
                            && !method.Parameters.Any(p => SymbolEqualityComparer.Default.Equals(p.Type, timeProvider)));
                    if (forbidden)
                    {
                        op.ReportDiagnostic(Diagnostic.Create(Rules.SleepInTests, op.Operation.Syntax.GetLocation(), $"{method.ContainingType.Name}.{method.Name}"));
                    }
                },
                OperationKind.Invocation);
        });
    }
}
