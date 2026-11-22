using Watchpoint.Configuration.Contracts;

namespace Watchpoint.Configuration.Internal.Validation;

/// <summary>
/// A draft as its validators see it: entries over the live configuration. Live reads are cached for the life of the view (one
/// validation pass), so a pass sees one consistent live state and asks each module once per object.
/// </summary>
internal sealed class DraftView(IConfigObjectTypeRegistry types, IReadOnlyList<DraftEntry> entries) : IDraftView
{
    private readonly Dictionary<ConfigObjectRef, DraftEntry> _entries = entries.ToDictionary(e => e.Ref);
    private readonly Dictionary<ConfigObjectRef, ConfigDocument?> _live = [];
    private List<(ConfigObjectRef Ref, IReadOnlyList<ConfigObjectRef> References)>? _liveReferences;

    public IReadOnlyCollection<DraftEntry> Entries => _entries.Values;

    public bool TryGetEntry(ConfigObjectRef reference, out DraftEntry? entry) => _entries.TryGetValue(reference, out entry);

    public async ValueTask<DraftObjectState> GetAsync(ConfigObjectRef reference, CancellationToken ct)
    {
        var live = await LiveAsync(reference, ct).ConfigureAwait(false);
        if (_entries.TryGetValue(reference, out var entry))
        {
            return entry.Operation switch
            {
                EntryOperation.Delete => new DraftObjectState(DraftObjectSource.Deleted, null, live?.Version),
                _ => new DraftObjectState(
                    DraftObjectSource.Draft,
                    entry.Content is { } content ? new ConfigDocument(reference, (live?.Version ?? 0) + 1, content) : null,
                    live?.Version),
            };
        }

        return live is null
            ? new DraftObjectState(DraftObjectSource.Missing, null, null)
            : new DraftObjectState(DraftObjectSource.Live, live, live.Version);
    }

    public async ValueTask<ConfigDocument?> LiveAsync(ConfigObjectRef reference, CancellationToken ct)
    {
        if (_live.TryGetValue(reference, out var cached))
        {
            return cached;
        }

        var type = types.Find(reference.Type);
        var document = type is null ? null : await type.LoadAsync(reference.Id, ct).ConfigureAwait(false);
        _live[reference] = document;
        return document;
    }

    /// <summary>The live objects the draft does not touch that refer to <paramref name="target"/>.</summary>
    public async ValueTask<IReadOnlyList<ConfigObjectRef>> LiveReferrersAsync(ConfigObjectRef target, CancellationToken ct)
    {
        if (_liveReferences is null)
        {
            List<(ConfigObjectRef, IReadOnlyList<ConfigObjectRef>)> all = [];
            foreach (var type in types.All)
            {
                await foreach (var document in type.ListAsync(new ConfigListQuery(), ct).ConfigureAwait(false))
                {
                    all.Add((document.Ref, type.GetReferences(document.Content)));
                }
            }

            _liveReferences = all;
        }

        return [.. _liveReferences
            .Where(r => !_entries.ContainsKey(r.Ref) && r.References.Contains(target))
            .Select(r => r.Ref)
            .OrderBy(r => r.Type, StringComparer.Ordinal)
            .ThenBy(r => r.Id)];
    }
}
