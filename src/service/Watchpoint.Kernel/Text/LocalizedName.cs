using System.Collections.Frozen;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Validation;

namespace Watchpoint.Kernel.Text;

/// <summary>
/// User content with optional translations (spec K §8.2). JSON:
/// <c>{ "default": "Main entrance", "translations": { "fr": "Entrée principale" } }</c>.
/// </summary>
/// <param name="Default">The text shown when no translation matches.</param>
/// <param name="Translations">Translations by culture (keys in <see cref="SupportedCultures.All"/>); may be empty.</param>
public sealed record LocalizedName(string Default, IReadOnlyDictionary<string, string> Translations)
{
    /// <summary>Creates a name without translations.</summary>
    /// <param name="default">The text.</param>
    /// <returns>The name.</returns>
    public static LocalizedName Of(string @default) => new(@default, FrozenDictionary<string, string>.Empty);

    /// <summary>Gets the text shown when no translation matches.</summary>
    public string Default { get; init; } = Default ?? throw new ArgumentNullException(nameof(Default));

    /// <summary>Gets the translations by culture; never <see langword="null"/>.</summary>
    public IReadOnlyDictionary<string, string> Translations { get; init; } =
        Translations ?? FrozenDictionary<string, string>.Empty;

    /// <summary>Returns the text for a culture: exact culture, then its neutral culture (<c>fr-CA</c> → <c>fr</c>), then <see cref="Default"/>.</summary>
    /// <param name="culture">The culture name, e.g. <c>fr-CA</c>.</param>
    /// <returns>The text.</returns>
    public string Resolve(string culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (Translations.TryGetValue(culture, out var exact))
        {
            return exact;
        }

        var neutral = SupportedCultures.Neutral(culture);
        return neutral.Length != culture.Length && Translations.TryGetValue(neutral, out var text) ? text : Default;
    }

    /// <summary>
    /// Validates the name: <see cref="Default"/> and every translation follow <see cref="NameRules"/>; translation
    /// keys must be supported cultures (<c>common.value.not_allowed</c> at <c>&lt;path&gt;/translations/&lt;key&gt;</c>).
    /// </summary>
    /// <param name="path">JSON Pointer of the name in the validated document (empty for the root).</param>
    /// <returns>The field errors; empty when valid.</returns>
    public IReadOnlyList<FieldError> Validate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var errors = new List<FieldError>(NameRules.Validate(Default, path + "/default"));
        foreach (var (culture, text) in Translations.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            var at = JsonPointer.Append(path + "/translations", culture);
            if (!SupportedCultures.IsSupported(culture))
            {
                errors.Add(CommonErrors.NotAllowedAt(at, SupportedCultures.All));
            }
            else
            {
                errors.AddRange(NameRules.Validate(text, at));
            }
        }

        return errors;
    }
}
