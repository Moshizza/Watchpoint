using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Watchpoint.Analyzers;

/// <summary>WP0004: <c>.Result</c>, <c>.Wait()</c> and <c>.GetAwaiter().GetResult()</c> on tasks are forbidden, except in <c>Main</c>.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BlockingWaitAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] _awaiterTypes =
    [
        "System.Runtime.CompilerServices.TaskAwaiter",
        "System.Runtime.CompilerServices.TaskAwaiter`1",
        "System.Runtime.CompilerServices.ValueTaskAwaiter",
        "System.Runtime.CompilerServices.ValueTaskAwaiter`1",
        "System.Runtime.CompilerServices.ConfiguredTaskAwaitable+ConfiguredTaskAwaiter",
        "System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter",
        "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter",
        "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1+ConfiguredValueTaskAwaiter",
    ];

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rules.BlockingWait);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var compilation = start.Compilation;
            var task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
            var taskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
            var valueTaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
            var awaiters = _awaiterTypes.Select(compilation.GetTypeByMetadataName).Where(t => t is not null).ToImmutableHashSet<INamedTypeSymbol?>(SymbolEqualityComparer.Default);

            bool Is(ITypeSymbol? type, INamedTypeSymbol? expected) =>
                expected is not null && type is not null && SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, expected);

            start.RegisterOperationAction(
                op =>
                {
                    var property = ((IPropertyReferenceOperation)op.Operation).Property;
                    if (property.Name == "Result" && (Is(property.ContainingType, taskOfT) || Is(property.ContainingType, valueTaskOfT)) && !InMain(op))
                    {
                        op.ReportDiagnostic(Diagnostic.Create(Rules.BlockingWait, op.Operation.Syntax.GetLocation(), ".Result"));
                    }
                },
                OperationKind.PropertyReference);

            start.RegisterOperationAction(
                op =>
                {
                    var method = ((IInvocationOperation)op.Operation).TargetMethod;
                    var blocking = (method.Name == "Wait" && !method.IsStatic && Is(method.ContainingType, task))
                        || (method.Name == "GetResult" && awaiters.Contains(method.ContainingType.OriginalDefinition));
                    if (blocking && !InMain(op))
                    {
                        op.ReportDiagnostic(Diagnostic.Create(Rules.BlockingWait, op.Operation.Syntax.GetLocation(), $".{method.Name}()"));
                    }
                },
                OperationKind.Invocation);
        });
    }

    private static bool InMain(OperationAnalysisContext context)
    {
        var symbol = context.ContainingSymbol;
        while (symbol is IMethodSymbol { MethodKind: MethodKind.LambdaMethod or MethodKind.LocalFunction })
        {
            symbol = symbol.ContainingSymbol;
        }

        return symbol is IMethodSymbol method
            && method.IsStatic
            && (method.Name is "Main" or "<Main>$" || SymbolEqualityComparer.Default.Equals(method, context.Compilation.GetEntryPoint(context.CancellationToken)));
    }
}
