namespace Watchpoint.Kernel.Text;

/// <summary>The user interface cultures of Watchpoint (spec K §8.3). Adding a culture is a change in this file only.</summary>
public static class SupportedCultures
{
    /// <summary>The default culture: <c>en</c>.</summary>
    public const string Default = "en";

    /// <summary>Gets all supported cultures (neutral culture names).</summary>
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(["en", "fr", "ar"]);

    /// <summary>Gets the right-to-left cultures.</summary>
    public static IReadOnlyList<string> RightToLeft { get; } = Array.AsReadOnly(["ar"]);

    /// <summary>Returns whether a culture name is in <see cref="All"/> (ordinal comparison).</summary>
    /// <param name="culture">The culture name.</param>
    /// <returns><see langword="true"/> when supported.</returns>
    public static bool IsSupported(string? culture) => culture is not null && All.Contains(culture, StringComparer.Ordinal);

    /// <summary>Returns whether a culture (or its neutral culture, <c>ar-MA</c> → <c>ar</c>) is written right to left.</summary>
    /// <param name="culture">The culture name.</param>
    /// <returns><see langword="true"/> for right-to-left cultures.</returns>
    public static bool IsRightToLeft(string? culture) =>
        culture is not null && RightToLeft.Contains(Neutral(culture), StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the neutral part of a culture name: <c>fr-CA</c> → <c>fr</c>.</summary>
    internal static string Neutral(string culture)
    {
        var dash = culture.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? culture : culture[..dash];
    }
}
