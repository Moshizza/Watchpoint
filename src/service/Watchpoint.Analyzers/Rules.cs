using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Watchpoint.Analyzers;

/// <summary>Diagnostic descriptors of the Watchpoint analyzers and shared helpers.</summary>
public static class Rules
{
    /// <summary>The MSBuild property (made compiler-visible by Directory.Build.props) set to <c>true</c> for projects under <c>tests/</c>.</summary>
    public const string TestCodeProperty = "build_property.WatchpointTestCode";

    private const string Category = "Watchpoint";
    private const string HelpLink = "https://github.com/Moshizza/Watchpoint/blob/main/Architecture/specs/TST-test-infrastructure.md#7-analyzers-watchpointanalyzers";

    /// <summary>WP0001: default or parameterless construction of <c>Result</c> / <c>Result&lt;T&gt;</c>.</summary>
    public static readonly DiagnosticDescriptor DefaultResult = Create(
        "WP0001",
        "Uninitialised Result",
        "'{0}' creates an uninitialised {1}; use Result.Success, Result.Failure or an implicit conversion");

    /// <summary>WP0002: test method name without a test id prefix.</summary>
    public static readonly DiagnosticDescriptor TestNaming = Create(
        "WP0002",
        "Test method name must start with a test id",
        "Test method '{0}' must start with a test id such as 'REG_U_012_' (<SPEC>_<LAYER>_<NNN>_)");

    /// <summary>WP0003: wall clock read outside <c>TimeProvider</c>.</summary>
    public static readonly DiagnosticDescriptor WallClock = Create(
        "WP0003",
        "Read time through TimeProvider",
        "'{0}' reads the wall clock; inject TimeProvider instead");

    /// <summary>WP0004: blocking wait on a task.</summary>
    public static readonly DiagnosticDescriptor BlockingWait = Create(
        "WP0004",
        "Do not block on tasks",
        "'{0}' blocks on a task; use await");

    /// <summary>WP0005: real-time sleep in tests.</summary>
    public static readonly DiagnosticDescriptor SleepInTests = Create(
        "WP0005",
        "Do not sleep in tests",
        "'{0}' waits in real time; use TestClock, a TimeProvider overload or a signal");

    /// <summary>Returns whether the analysed project is test code (under <c>tests/</c>).</summary>
    /// <param name="options">The analyzer options.</param>
    /// <returns><see langword="true"/> for test code.</returns>
    public static bool IsTestCode(AnalyzerOptions options) =>
        options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(TestCodeProperty, out var value)
        && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static DiagnosticDescriptor Create(string id, string title, string message) =>
        new(id, title, message, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
}
