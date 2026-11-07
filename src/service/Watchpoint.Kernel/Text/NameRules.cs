using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Kernel.Text;

/// <summary>
/// Rules for display names of devices, servers, locations, users, rules… (spec K §8.4): length 1–200 after NFC
/// normalisation, no control characters, no leading or trailing whitespace.
/// </summary>
public static class NameRules
{
    /// <summary>The minimum length.</summary>
    public const int MinLength = 1;

    /// <summary>The maximum length (UTF-16 code units, after NFC normalisation).</summary>
    public const int MaxLength = 200;

    private const CompareOptions UniquenessOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    /// <summary>Returns the NFC-normalised form of a name, the form to store.</summary>
    /// <param name="value">The name.</param>
    /// <returns>The normalised name.</returns>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.IsNormalized(NormalizationForm.FormC) ? value : value.Normalize(NormalizationForm.FormC);
    }

    /// <summary>Validates a name.</summary>
    /// <param name="value">The name.</param>
    /// <param name="path">JSON Pointer of the field, for the field errors.</param>
    /// <returns>
    /// No error when valid; otherwise <c>common.value.required</c> (null, empty or blank),
    /// <c>common.value.too_long</c>, or <c>common.value.invalid_format</c> (control characters, leading or trailing
    /// whitespace).
    /// </returns>
    public static IReadOnlyList<FieldError> Validate(string? value, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(value))
        {
            return [CommonErrors.RequiredAt(path)];
        }

        var errors = new List<FieldError>();
        var normalized = Normalize(value);
        if (normalized.Length > MaxLength)
        {
            errors.Add(CommonErrors.TooLongAt(path, MaxLength));
        }

        if (normalized.Any(char.IsControl))
        {
            errors.Add(CommonErrors.InvalidFormatAt(path, "no control characters"));
        }

        if (char.IsWhiteSpace(normalized[0]) || char.IsWhiteSpace(normalized[^1]))
        {
            errors.Add(CommonErrors.InvalidFormatAt(path, "no leading or trailing whitespace"));
        }

        return errors;
    }

    /// <summary>
    /// Compares two names for uniqueness: NFC, invariant culture, ignoring case and diacritics (<c>é</c> equals
    /// <c>É</c> and <c>E</c>).
    /// </summary>
    /// <param name="a">The first name.</param>
    /// <param name="b">The second name.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    [SuppressMessage("Globalization", "CA1309:Use ordinal string comparison", Justification = "Linguistic comparison required by spec K §8.4.")]
    public static int Compare(string a, string b) =>
        string.Compare(Normalize(a), Normalize(b), CultureInfo.InvariantCulture, UniquenessOptions);

    /// <summary>Returns whether two names are the same for uniqueness (<see cref="Compare"/> returns zero).</summary>
    /// <param name="a">The first name.</param>
    /// <param name="b">The second name.</param>
    /// <returns><see langword="true"/> when equivalent.</returns>
    public static bool AreEquivalent(string a, string b) => Compare(a, b) == 0;
}
