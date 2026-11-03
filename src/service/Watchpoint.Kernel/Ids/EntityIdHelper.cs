using System.Diagnostics.CodeAnalysis;

namespace Watchpoint.Kernel.Ids;

/// <summary>Shared implementation of the UUID-based id types.</summary>
internal static class EntityIdHelper
{
    public static Guid EnsureNotEmpty(Guid value) =>
        value == Guid.Empty ? throw new ArgumentException("Empty id", nameof(value)) : value;

    public static bool TryParse([NotNullWhen(true)] string? s, out Guid value)
    {
        // "D" only: 36 characters with dashes, upper or lower case; no braces, no parentheses, no compact form.
        if (s is { Length: 36 } && Guid.TryParseExact(s, "D", out value) && value != Guid.Empty)
        {
            return true;
        }

        value = Guid.Empty;
        return false;
    }

    public static string Format(Guid value) => value.ToString("D");

    public static bool TryFormat(Guid value, Span<char> destination, out int charsWritten) =>
        value.TryFormat(destination, out charsWritten, "D");
}
