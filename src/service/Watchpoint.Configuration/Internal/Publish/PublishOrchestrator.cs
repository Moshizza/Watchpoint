using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Configuration.Internal.Drafts;
using Watchpoint.Configuration.Internal.Versions;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Configuration.Internal.Publish;

/// <summary>
/// Publishes change sets (spec CFG §7.1): the draft is locked (<c>Publishing</c>), the publish lock taken, the draft
/// validated again against the live configuration, base versions checked, then every module applies its entries in one
/// transaction of the configuration database (catalogue order), with the version row, its inverse and its diff. After
/// the commit the modules update their memory, <c>config.published</c> is appended and the publish audited. A failure
/// before the commit rolls everything back; nothing is applied.
/// </summary>
internal sealed class PublishOrchestrator : IPublishService, IDisposable
{
    private readonly IDraftRepository _drafts;
    private readonly DraftService _draftService;
    private readonly IConfigObjectTypeRegistry _types;
    private readonly Dictionary<string, IConfigApplier> _appliers;
    private readonly IPublishStore _store;
    private readonly IConfigPublishedSink? _sink;
    private readonly IConfigAudit? _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<PublishOrchestrator> _logger;
    private readonly Meter _meter;
    private readonly Counter<long> _publishes;
    private readonly Histogram<double> _duration;

    public PublishOrchestrator(IDraftRepository drafts, DraftService draftService, IConfigObjectTypeRegistry types, IEnumerable<IConfigApplier> appliers,
        IPublishStore store, TimeProvider time, ILogger<PublishOrchestrator> logger, IConfigPublishedSink? sink = null, IConfigAudit? audit = null,
        IMeterFactory? meters = null)
    {
        _drafts = drafts;
        _draftService = draftService;
        _types = types;
        _appliers = appliers.ToDictionary(a => a.Module, StringComparer.Ordinal);
        _store = store;
        _sink = sink;
        _audit = audit;
        _time = time;
        _logger = logger;
        _meter = meters?.Create("Watchpoint.Configuration") ?? new Meter("Watchpoint.Configuration");
        _publishes = _meter.CreateCounter<long>("watchpoint.config.publishes", "{publishes}", "Publishes, by outcome.");
        _duration = _meter.CreateHistogram<double>("watchpoint.config.publish.duration", "ms", "Duration of successful publishes.");
    }

    /// <summary>The node that publishes (in <c>config.published</c>; other nodes reload, M9).</summary>
    public Guid? Node { get; init; }

    public async Task<Result<long>> PublishAsync(ChangeSet changeSet, UserId actor, string? comment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        var started = _time.GetTimestamp();
        var result = await PublishCoreAsync(changeSet, actor, comment, ct).ConfigureAwait(false);
        var outcome = result.IsSuccess ? "published" : result.Error.Code.Value switch
        {
            ConfigurationErrors.PublishFailed => "failed",
            ConfigurationErrors.PublishConflict => "conflict",
            ConfigurationErrors.PublishInvalid => "invalid",
            _ => "rejected",
        };
        _publishes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        if (result.IsSuccess)
        {
            _duration.Record(_time.GetElapsedTime(started).TotalMilliseconds);
        }
        else if (outcome != "failed")
        {
            ConfigurationLog.Rejected(_logger, changeSet.Draft.Value, result.Error.Code.Value);
        }

        return result;
    }

    public void Dispose() => _meter.Dispose();

    private async Task<Result<long>> PublishCoreAsync(ChangeSet changeSet, UserId actor, string? comment, CancellationToken ct)
    {
        // Every module must be able to apply its entries, before anything is locked.
        foreach (var entry in changeSet.Entries)
        {
            if (_types.Find(entry.Ref.Type) is not { } type)
            {
                return Error.Create(ErrorKind.Validation, ConfigurationErrors.EntryInvalid, ("reason", "unknown_type"), ("type", entry.Ref.Type));
            }

            if (!_appliers.ContainsKey(type.Module))
            {
                return Error.Create(ErrorKind.Unprocessable, ConfigurationErrors.PublishNotSupported, ("type", type.Type), ("module", type.Module));
            }
        }

        // Step 1: lock the draft.
        if (await _drafts.GetAsync(changeSet.Draft, ct).ConfigureAwait(false) is not { } draft)
        {
            return ConfigurationErrors.NotFound(changeSet.Draft.Value);
        }

        if (draft.Status == DraftStatus.Publishing)
        {
            return Error.Create(ErrorKind.Conflict, ConfigurationErrors.PublishInProgress, ("draftId", draft.Id.Value));
        }

        if (draft.Status != DraftStatus.Open)
        {
            return ConfigurationErrors.NotOpen(draft.Id.Value, draft.Status.ToString());
        }

        if (draft.Version != changeSet.DraftVersion)
        {
            return ConfigurationErrors.VersionConflict(changeSet.DraftVersion, draft.Version);
        }

        var publishing = draft with { Status = DraftStatus.Publishing, UpdatedAt = _time.GetUtcNow(), Version = draft.Version + 1 };
        if (!await _drafts.TryReplaceAsync(publishing, draft.Version, null, ct).ConfigureAwait(false))
        {
            return ConfigurationErrors.VersionConflict(draft.Version, (await _drafts.GetAsync(draft.Id, ct).ConfigureAwait(false))?.Version ?? -1);
        }

        IPublishTransaction? transaction = null;
        ConfigApplyContext? prepared = null;
        var appliedSoFar = new List<(IConfigApplier Applier, IReadOnlyList<AppliedEntry> Entries)>();
        var committed = false;
        try
        {
            // Step 1 (cont.): one publish at a time per site.
            transaction = await _store.BeginAsync(ct).ConfigureAwait(false);

            // Step 2: validate again against the live configuration as it is now.
            var problems = await _draftService.ValidateAllAsync(changeSet.Entries, null, ct).ConfigureAwait(false);
            var errors = problems.Count(p => p.Severity == ProblemSeverity.Error);
            if (errors > 0)
            {
                await ReopenAsync(publishing, problems, ct).ConfigureAwait(false);
                return Error.Create(ErrorKind.Unprocessable, ConfigurationErrors.PublishInvalid, ("draftId", draft.Id.Value), ("errors", errors));
            }

            // Step 3: the base version of every update and delete is still the live one.
            var conflicts = await ConflictsAsync(changeSet.Entries, ct).ConfigureAwait(false);
            if (conflicts.Count > 0)
            {
                await ReopenAsync(publishing, problems, ct).ConfigureAwait(false);
                return Error.Create(ErrorKind.Conflict, ConfigurationErrors.PublishConflict, ("draftId", draft.Id.Value), ("count", conflicts.Count),
                    ("objects", conflicts));
            }

            // Steps 4–5: every module in catalogue order, then the version row, in one transaction.
            var version = await transaction.NextVersionAsync(ct).ConfigureAwait(false);
            var context = new ConfigApplyContext(actor, changeSet.Id, version, comment);
            var applied = appliedSoFar;
            foreach (var (module, entries) in ByModule(changeSet.Entries))
            {
                var applier = _appliers[module];
                prepared = context;
                if (!(await applier.PrepareApplyAsync(entries, transaction.Connection, transaction.Transaction, context, ct).ConfigureAwait(false))
                    .TryGetValue(out var done, out var refused))
                {
                    await AbortAsync(applied, context).ConfigureAwait(false);
                    return await FailAsync(publishing, transaction, refused.Code.Value, null, ct).ConfigureAwait(false);
                }

                applied.Add((applier, done));
            }

            var all = applied.SelectMany(a => a.Entries).ToList();
            var now = _time.GetUtcNow();
            var row = new ConfigVersion(version, now, actor, comment, draft.Id, Summary(all));
            await transaction.WriteAsync(new VersionRecord(row, changeSet.Id, all, Inverse(all), Diff(all)), ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            committed = true;
            await transaction.DisposeAsync().ConfigureAwait(false);
            transaction = null;

            // Step 6: after the commit.
            await CompleteAsync(applied, context, ct).ConfigureAwait(false);
            await AnnounceAsync(row, all, actor, ct).ConfigureAwait(false);
            var published = publishing with { Status = DraftStatus.Published, Problems = [], UpdatedAt = now, Version = publishing.Version + 1 };
            await _drafts.TryReplaceAsync(published, publishing.Version, null, CancellationToken.None).ConfigureAwait(false);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                var summary = string.Join(", ", row.Summary.Select(s => s.Key + "=" + s.Value));
                ConfigurationLog.Published(_logger, draft.Id.Value, version, actor.Value, summary);
            }
            return version;
        }
        catch (Exception ex) when (!committed && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            // Step 7: rolled back, nothing applied.
            if (prepared is not null)
            {
                await AbortAsync(appliedSoFar, prepared).ConfigureAwait(false);
            }

            return await FailAsync(publishing, transaction, ex.GetType().Name, ex, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!committed)
        {
            if (prepared is not null)
            {
                await AbortAsync(appliedSoFar, prepared).ConfigureAwait(false);
            }

            await FailAsync(publishing, transaction, "cancelled", null, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Entries grouped by module, modules in catalogue order (first type of each module), entries in change-set order.</summary>
    private List<(string Module, IReadOnlyList<DraftEntry> Entries)> ByModule(IReadOnlyList<DraftEntry> entries)
    {
        var modules = _types.All.Select(t => t.Module).Distinct(StringComparer.Ordinal).ToList();
        var moduleOf = _types.All.ToDictionary(t => t.Type, t => t.Module, StringComparer.Ordinal);
        return [.. modules
            .Select(m => (m, (IReadOnlyList<DraftEntry>)[.. entries.Where(e => moduleOf[e.Ref.Type] == m)]))
            .Where(g => g.Item2.Count > 0)];
    }

    private async Task<List<string>> ConflictsAsync(IReadOnlyList<DraftEntry> entries, CancellationToken ct)
    {
        var conflicts = new List<string>();
        foreach (var entry in entries.Where(e => e.Operation != EntryOperation.Create))
        {
            var live = await _types.Find(entry.Ref.Type)!.LoadAsync(entry.Ref.Id, ct).ConfigureAwait(false);
            if (live?.Version != entry.BaseVersion)
            {
                conflicts.Add(entry.Ref.ToString());
            }
        }

        return conflicts;
    }

    /// <summary>The transaction is (or will be) rolled back: the modules that prepared forget their entries.</summary>
    private async Task AbortAsync(List<(IConfigApplier Applier, IReadOnlyList<AppliedEntry> Entries)> applied, ConfigApplyContext context)
    {
        foreach (var (applier, _) in applied)
        {
            try
            {
                await applier.AbortApplyAsync(context, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ConfigurationLog.Failed(_logger, ex, Guid.Empty, "abort " + applier.Module);
            }
        }

        applied.Clear();
    }

    private async Task CompleteAsync(List<(IConfigApplier Applier, IReadOnlyList<AppliedEntry> Entries)> applied, ConfigApplyContext context, CancellationToken ct)
    {
        foreach (var (applier, entries) in applied)
        {
            try
            {
                await applier.CompleteApplyAsync(entries, context, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Self-healing: the committed state is reloaded from the database.
                ConfigurationLog.CompleteFailed(_logger, ex, applier.Module, context.Version);
                try
                {
                    await applier.ReloadAsync([.. entries.Select(e => e.Ref)], CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception reload)
                {
                    ConfigurationLog.ReloadFailed(_logger, reload, applier.Module, context.Version);
                }
            }
        }
    }

    private async Task AnnounceAsync(ConfigVersion row, List<AppliedEntry> all, UserId actor, CancellationToken ct)
    {
        try
        {
            if (_sink is not null)
            {
                var body = new ConfigPublishedBody(row.Version, row.Draft, [.. all.Select(e => new PublishedChange(e.Ref.Type, e.Ref.Id, e.Operation, e.NewVersion))], Node);
                await _sink.AppendAsync(body, actor, row.PublishedAt, ct).ConfigureAwait(false);
            }

            if (_audit is not null)
            {
                await _audit.PublishedAsync(row, all, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Committed: the version stands; other nodes catch up on their next reload.
            ConfigurationLog.AnnounceFailed(_logger, ex, row.Version);
        }
    }

    private async Task ReopenAsync(Draft publishing, IReadOnlyList<Problem> problems, CancellationToken ct)
    {
        var open = publishing with { Status = DraftStatus.Open, Problems = problems, UpdatedAt = _time.GetUtcNow(), Version = publishing.Version + 1 };
        await _drafts.TryReplaceAsync(open, publishing.Version, null, ct).ConfigureAwait(false);
    }

    private async Task<Result<long>> FailAsync(Draft publishing, IPublishTransaction? transaction, string reason, Exception? exception, CancellationToken ct)
    {
        if (transaction is not null)
        {
            await transaction.DisposeAsync().ConfigureAwait(false); // rolls back
        }

        ConfigurationLog.Failed(_logger, exception, publishing.Id.Value, reason);
        var problem = new Problem(ProblemSeverity.Error, ConfigurationErrors.PublishFailed, new Dictionary<string, object?> { ["reason"] = reason }, null, null, []);
        var failed = publishing with { Status = DraftStatus.Failed, Problems = [problem], UpdatedAt = _time.GetUtcNow(), Version = publishing.Version + 1 };
        await _drafts.TryReplaceAsync(failed, publishing.Version, null, ct == default ? CancellationToken.None : ct).ConfigureAwait(false);
        return Error.Create(ErrorKind.Internal, ConfigurationErrors.PublishFailed, ("draftId", publishing.Id.Value), ("reason", reason));
    }

    /// <summary>Entries per <c>type:operation</c>.</summary>
    internal static Dictionary<string, int> Summary(IEnumerable<AppliedEntry> entries) =>
        entries.GroupBy(e => e.Ref.Type + ":" + e.Operation.ToString().ToLowerInvariant(), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    /// <summary>The entries that undo <paramref name="applied"/> (spec CFG §7.3), in reverse order, based on the versions it produced.</summary>
    internal static List<DraftEntry> Inverse(IReadOnlyList<AppliedEntry> applied) =>
        [.. applied.Reverse().Select(e => e.Operation switch
        {
            EntryOperation.Create => new DraftEntry(Guid.CreateVersion7(), e.Ref, EntryOperation.Delete, e.NewVersion, null, default),
            EntryOperation.Update => new DraftEntry(Guid.CreateVersion7(), e.Ref, EntryOperation.Update, e.NewVersion, e.Before, default),
            _ => new DraftEntry(Guid.CreateVersion7(), e.Ref, EntryOperation.Create, null, e.Before, default),
        })];

    internal static List<DiffEntry> Diff(IReadOnlyList<AppliedEntry> applied) =>
        [.. applied.Select(e => new DiffEntry(e.Ref, e.Operation, JsonDiff.Diff(e.Before, e.After)))];
}
