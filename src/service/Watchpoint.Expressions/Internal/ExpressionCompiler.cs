using System.Text.Encodings.Web;
using System.Text.Json;
using Watchpoint.Expressions.Contracts;

namespace Watchpoint.Expressions.Internal;

internal sealed class ExpressionCompiler : IExpressionCompiler
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public CompileResult Compile(string source, ExpressionEnvironment environment, ExpectedType expected)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(environment);

        var diagnostics = new List<Diagnostic>();

        // 1. Lexer
        var lexer = new Lexer(source);
        var tokens = lexer.Tokenize();
        diagnostics.AddRange(lexer.Diagnostics);

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return new CompileResult(null, diagnostics);
        }

        // 2. Parser
        var parser = new Parser(tokens);
        var ast = parser.Parse();
        diagnostics.AddRange(parser.Diagnostics);

        if (ast is null || diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return new CompileResult(null, diagnostics);
        }

        // 3. Type Checker
        var checker = new TypeChecker(environment, expected);
        var resultType = checker.Check(ast);
        diagnostics.AddRange(checker.Diagnostics);

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return new CompileResult(null, diagnostics);
        }

        // 4. Generate canonical AST JSON
        var astJson = ast.ToJson().ToJsonString(JsonOptions);

        // 5. Build CompiledExpression
        var compiled = new CompiledExpression(
            source,
            resultType,
            checker.ReferencedProperties,
            checker.ReferencedRefs,
            checker.UsesNow,
            checker.ReferencedCalendars,
            ctx =>
            {
                var evaluator = new Evaluator(ctx);
                return evaluator.Evaluate(ast);
            },
            astJson);

        return new CompileResult(compiled, diagnostics);
    }
}
