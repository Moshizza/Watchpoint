using System.Globalization;

namespace Watchpoint.Integration.Abstractions;

/// <summary>Version of the integration contract and the compatibility rules of manifests (INT §11).</summary>
public static class ContractVersion
{
    /// <summary>The contract version implemented by this assembly.</summary>
    public const string Current = "1.0";

    /// <summary>Parses a contract version <c>major.minor</c> (both non-negative integers).</summary>
    /// <param name="text">The text, e.g. <c>1.3</c>.</param>
    /// <param name="major">The major version.</param>
    /// <param name="minor">The minor version.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a valid version.</returns>
    public static bool TryParse(string? text, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        return dot > 0
            && TryParsePart(text.AsSpan(0, dot), out major)
            && TryParsePart(text.AsSpan(dot + 1), out minor);
    }

    /// <summary>
    /// Checks whether a contract version satisfies a manifest requirement. A requirement is <c>^major.minor</c>
    /// (same major, at least that minor) or an exact <c>major.minor</c>.
    /// </summary>
    /// <param name="requirement">The manifest's <c>contract</c> value, e.g. <c>^1.0</c>.</param>
    /// <param name="version">The version to check, e.g. <c>1.3</c>.</param>
    /// <returns><see langword="true"/> when both are valid and <paramref name="version"/> satisfies the requirement.</returns>
    public static bool Satisfies(string? requirement, string? version)
    {
        if (requirement is null || !TryParse(version, out var major, out var minor))
        {
            return false;
        }

        var caret = requirement.StartsWith('^');
        if (!TryParse(caret ? requirement[1..] : requirement, out var requiredMajor, out var requiredMinor))
        {
            return false;
        }

        return caret
            ? major == requiredMajor && minor >= requiredMinor
            : major == requiredMajor && minor == requiredMinor;
    }

    /// <summary>
    /// Checks whether a package requiring <paramref name="requirement"/> can run on this contract
    /// (<see cref="Current"/>): known major and a minor not newer than the one implemented.
    /// </summary>
    /// <param name="requirement">The manifest's <c>contract</c> value.</param>
    /// <returns><see langword="true"/> when the runtime implements the requirement.</returns>
    public static bool IsSupported(string? requirement) => Satisfies(requirement, Current);

    private static bool TryParsePart(ReadOnlySpan<char> part, out int value)
    {
        value = 0;
        return part.Length is > 0 and <= 4
            && !part.ContainsAnyExceptInRange('0', '9')
            && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
