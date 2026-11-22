using System.Text.Json;
using Watchpoint.Configuration.Contracts;

namespace Watchpoint.Configuration.Internal.Validation;

/// <summary>
/// Validates draft entries (spec CFG §5). The result is a function of the entries and of the live configuration the view reads:
/// the same inputs give the same problems in the same order, and problems are data (code, arguments, JSON pointer), never exception
/// text. Order: object type, object id, path, code.
/// </summary>
internal sealed class DraftValidator(IConfigObjectTypeRegistry types, ILicenceProjection? licence)
{
    /// <summary>How many referrers a <c>config.reference.in_use</c> problem lists.</summary>
    public const int MaxListedReferrers = 10;

    /// <summary>Validates the given entries (and nothing else).</summary>
    /// <param name="view">The draft view.</param>
    /// <param name="subjects">The entries to check.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The problems of these entries.</returns>
    public async Task<IReadOnlyList<Problem>> ValidateAsync(DraftView view, IEnumerable<DraftEntry> subjects, CancellationToken ct)
    {
        List<Problem> problems = [];
        foreach (var entry in subjects)
        {
            problems.AddRange(await ValidateEntryAsync(view, entry, ct).ConfigureAwait(false));
        }

        return Sort(problems);
    }

    /// <summary>Validates what only the whole draft decides: the licence projection.</summary>
    /// <param name="view">The draft view.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The problems.</returns>
    public async Task<IReadOnlyList<Problem>> ValidateWholeAsync(DraftView view, CancellationToken ct)
    {
        if (licence is null)
        {
            return [];
        }

        return Sort(await licence.ProjectAsync([.. view.Entries], view, ct).ConfigureAwait(false));
    }

    /// <summary>Orders problems deterministically.</summary>
    /// <param name="problems">The problems.</param>
    /// <returns>The problems, sorted.</returns>
    public static IReadOnlyList<Problem> Sort(IEnumerable<Problem> problems) => [.. problems
        .OrderBy(p => p.Object?.Type, StringComparer.Ordinal)
        .ThenBy(p => p.Object?.Id)
        .ThenBy(p => p.Path, StringComparer.Ordinal)
        .ThenBy(p => p.Code, StringComparer.Ordinal)
        .ThenBy(p => p.Severity)];

    private async Task<IReadOnlyList<Problem>> ValidateEntryAsync(DraftView view, DraftEntry entry, CancellationToken ct)
    {
        List<Problem> problems = [];
        var type = types.Find(entry.Ref.Type);
        if (type is null)
        {
            problems.Add(Problem(ConfigurationErrors.EntryInvalid, entry.Ref, null, ("reason", "unknown_type"), ("type", entry.Ref.Type)));
            return problems;
        }

        var live = await view.LiveAsync(entry.Ref, ct).ConfigureAwait(false);
        switch (entry.Operation)
        {
            case EntryOperation.Create when live is not null:
                problems.Add(Problem(ConfigurationErrors.EntryInvalid, entry.Ref, null, ("reason", "already_exists")));
                break;
            case EntryOperation.Update or EntryOperation.Delete when live is null:
                problems.Add(Problem(ConfigurationErrors.EntryInvalid, entry.Ref, null, ("reason", "not_found")));
                break;
        }

        if (entry.Operation != EntryOperation.Create && live is not null && entry.BaseVersion is { } baseVersion && baseVersion != live.Version)
        {
            problems.Add(Problem(
                ConfigurationErrors.EntryOutdated, entry.Ref, null, ("baseVersion", baseVersion), ("liveVersion", live.Version)) with
            { Severity = ProblemSeverity.Warning });
        }

        if (entry.Operation == EntryOperation.Delete)
        {
            if (entry.Content is not null)
            {
                problems.Add(Problem(ConfigurationErrors.EntryInvalid, entry.Ref, null, ("reason", "content_not_allowed")));
            }

            var referrers = await view.LiveReferrersAsync(entry.Ref, ct).ConfigureAwait(false);
            if (referrers.Count > 0)
            {
                problems.Add(Problem(
                    ConfigurationErrors.ReferenceInUse, entry.Ref, null, ("count", referrers.Count), ("referrers", referrers.Take(MaxListedReferrers).Select(r => r.ToString()).ToList())));
            }

            return problems;
        }

        if (entry.Content is not { ValueKind: JsonValueKind.Object } content)
        {
            problems.Add(Problem(ConfigurationErrors.EntryInvalid, entry.Ref, null, ("reason", "content_required")));
            return problems;
        }

        if (type.Schema is { } schema)
        {
            problems.AddRange(DocumentSchemaValidator.Validate(schema, content, entry.Ref));
        }

        foreach (var problem in await type.ValidateAsync(entry, view, ct).ConfigureAwait(false))
        {
            problems.Add(problem with { Object = problem.Object ?? entry.Ref });
        }

        foreach (var reference in type.GetReferences(content).Distinct())
        {
            var state = await view.GetAsync(reference, ct).ConfigureAwait(false);
            if (state.Source is DraftObjectSource.Missing or DraftObjectSource.Deleted)
            {
                problems.Add(Problem(
                    ConfigurationErrors.ReferenceMissing,
                    entry.Ref,
                    null,
                    ("type", reference.Type),
                    ("id", reference.Id),
                    ("deletedInDraft", state.Source == DraftObjectSource.Deleted)));
            }
        }

        return problems;
    }

    private static Problem Problem(string code, ConfigObjectRef subject, string? path, params (string Name, object? Value)[] args) =>
        new(ProblemSeverity.Error, code, Args.Of(args), subject, path, []);
}
