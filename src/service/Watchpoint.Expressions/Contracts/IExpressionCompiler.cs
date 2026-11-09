namespace Watchpoint.Expressions.Contracts;

/// <summary>Compiles, type-checks, and validates CEL subset expressions against an environment (spec EXP §5).</summary>
public interface IExpressionCompiler
{
    /// <summary>Compiles an expression string within the given environment and checks for the expected return type.</summary>
    /// <param name="source">The expression text.</param>
    /// <param name="environment">The environment declaring types, variables, and available models.</param>
    /// <param name="expected">The expected result type (<see cref="ExpectedType.Bool"/> or <see cref="ExpectedType.Any"/>).</param>
    /// <returns>The compilation result containing the compiled expression and diagnostics.</returns>
    CompileResult Compile(string source, ExpressionEnvironment environment, ExpectedType expected);
}
