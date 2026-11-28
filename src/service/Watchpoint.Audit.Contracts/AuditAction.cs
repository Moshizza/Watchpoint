using System.Text.RegularExpressions;

namespace Watchpoint.Audit.Contracts;

/// <summary>An audited action, <c>&lt;module&gt;.&lt;verb&gt;</c>, for example <c>identity.signedIn</c>.</summary>
/// <param name="Value">The action text.</param>
public readonly partial record struct AuditAction(string Value)
{
    /// <summary>The action an invalid value is normalised to in Release builds (<c>audit.action.invalid</c>).</summary>
    public static AuditAction Unknown { get; } = new("unknown.invalid");

    /// <summary>Gets whether <paramref name="value"/> matches <c>^[a-z]+(\.[a-zA-Z]+)+$</c>.</summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true"/> when valid.</returns>
    public static bool IsValid(string? value) => value is { Length: > 0 and <= 128 } && ActionFormat().IsMatch(value);

    /// <summary>The translation key of the action label, <c>audit.action.&lt;module&gt;.&lt;verb&gt;</c>.</summary>
    public string TranslationKey => "audit.action." + Value;

    /// <inheritdoc />
    public override string ToString() => Value;

    [GeneratedRegex(@"^[a-z]+(\.[a-zA-Z]+)+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ActionFormat();
}
