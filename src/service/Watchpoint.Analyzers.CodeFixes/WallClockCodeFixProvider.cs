using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Watchpoint.Analyzers;

/// <summary>
/// Code fix for WP0003: replaces <c>DateTime(Offset).Now/UtcNow</c> with a call on the <c>TimeProvider</c> in scope
/// (field, property, parameter or local), or on <c>TimeProvider.System</c> when none is in scope.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(WallClockCodeFixProvider))]
[Shared]
public sealed class WallClockCodeFixProvider : CodeFixProvider
{
    private const string Title = "Use TimeProvider";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(Rules.WallClock.Id);

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            if (root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not MemberAccessExpressionSyntax access
                || model.GetSymbolInfo(access, context.CancellationToken).Symbol is not IPropertySymbol property)
            {
                continue;
            }

            var call = (property.ContainingType.SpecialType == SpecialType.System_DateTime, property.Name) switch
            {
                (false, "UtcNow") => "GetUtcNow()",
                (false, "Now") => "GetLocalNow()",
                (true, "UtcNow") => "GetUtcNow().UtcDateTime",
                (true, "Now") => "GetLocalNow().DateTime",
                _ => null,
            };
            if (call is null)
            {
                continue;
            }

            var provider = FindTimeProvider(model, access) ?? "TimeProvider.System";
            var replacement = SyntaxFactory.ParseExpression($"{provider}.{call}").WithTriviaFrom(access);
            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(access, replacement))),
                    equivalenceKey: Title),
                diagnostic);
        }
    }

    private static string? FindTimeProvider(SemanticModel model, SyntaxNode node)
    {
        var timeProvider = model.Compilation.GetTypeByMetadataName("System.TimeProvider");
        if (timeProvider is null)
        {
            return null;
        }

        var enclosing = model.GetEnclosingSymbol(node.SpanStart);
        var inStaticContext = enclosing is { IsStatic: true };
        foreach (var symbol in model.LookupSymbols(node.SpanStart))
        {
            var (type, needsInstance) = symbol switch
            {
                ILocalSymbol local => (local.Type, false),
                // A primary constructor parameter is captured state: not usable from static members.
                IParameterSymbol parameter => (parameter.Type, parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor
                    && !SymbolEqualityComparer.Default.Equals(ctor, enclosing)),
                IFieldSymbol field => (field.Type, !field.IsStatic),
                IPropertySymbol prop => (prop.Type, !prop.IsStatic),
                _ => ((ITypeSymbol?)null, false),
            };
            if (type is null || (needsInstance && inStaticContext))
            {
                continue;
            }

            for (var t = type; t is not null; t = t.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(t, timeProvider))
                {
                    return symbol.Name;
                }
            }
        }

        return null;
    }
}
