using System.Text.Json;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Configuration.Internal.Validation;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;

namespace Watchpoint.Configuration.Internal.Drafts;

/// <summary>
/// Drafts (spec CFG §5–§6). A draft is only ever changed from the version the editor read: the store replaces it when its
/// version is still that one, so two editors cannot overwrite each other. Nothing here touches the live configuration.
/// </summary>
internal sealed class DraftService(
    IDraftRepository repository,
    IConfigObjectTypeRegistry types,
    IConfigurationAuthorizer authorizer,
    DraftSecrets secrets,
    DraftValidator validator,
    ConfigurationSettings settings,
    TimeProvider time) : IDraftService
{
    public async Task<Result<Draft>> CreateAsync(LocalizedName name, UserId owner, CancellationToken ct) =>
        await CreateCoreAsync(name, owner, hidden: false, ct).ConfigureAwait(false);

    public async Task<Result<Draft>> GetAsync(DraftId id, UserId actor, CancellationToken ct) => await VisibleAsync(id, actor, ct).ConfigureAwait(false);

    public Task<IReadOnlyList<Draft>> ListAsync(UserId actor, bool mine, CancellationToken ct) => repository.ListAsync(actor, mine, ct);

    public async Task<Result<EntryResult>> SetEntryAsync(DraftId id, UserId actor, long expectedVersion, EntryRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!(await OpenAsync(id, actor, expectedVersion, ct).ConfigureAwait(false)).TryGetValue(out var draft, out var error))
        {
            return error;
        }

        if (types.Find(request.Ref.Type) is not { } type)
        {
            return Error.Create(ErrorKind.Validation, ConfigurationErrors.EntryInvalid, ("reason", "unknown_type"), ("type", request.Ref.Type));
        }

        var permission = type.PermissionFor(ConfigAccess.Edit);
        if (!await authorizer.IsAllowedAsync(actor, permission, request.Ref, ct).ConfigureAwait(false))
        {
            return ConfigurationErrors.Forbidden(permission.Value);
        }

        var existing = draft.Entries.FirstOrDefault(e => e.Ref == request.Ref);
        if (existing is null && draft.Entries.Count >= settings.MaxDraftEntries)
        {
            return ConfigurationErrors.TooLarge(settings.MaxDraftEntries);
        }

        // Secrets of the new content go to the vault first; they are taken back when the change does not go through.
        var entryId = existing?.EntryId ?? Guid.CreateVersion7();
        JsonElement? content = request.Operation == EntryOperation.Delete ? null : request.Content;
        IReadOnlyList<DraftSecretRef> created = [];
        if (content is { ValueKind: JsonValueKind.Object } raw)
        {
            if (!(await secrets.StoreAsync(id, entryId, raw, ct).ConfigureAwait(false)).TryGetValue(out var stored, out var secretError))
            {
                return secretError;
            }

            (content, created) = (stored.Content, stored.Created);
        }

        var (entries, entry) = Merge(draft.Entries, existing, entryId, request, content);
        var result = await CommitAsync(draft, entries, entry, request.Ref, created, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return result;
        }

        await secrets.DeleteAsync(id, created, ct).ConfigureAwait(false);
        return result;
    }

    public async Task<Result<Draft>> RemoveEntryAsync(DraftId id, UserId actor, long expectedVersion, Guid entryId, CancellationToken ct)
    {
        if (!(await OpenAsync(id, actor, expectedVersion, ct).ConfigureAwait(false)).TryGetValue(out var draft, out var error))
        {
            return error;
        }

        var existing = draft.Entries.FirstOrDefault(e => e.EntryId == entryId);
        if (existing is null)
        {
            return Error.Create(ErrorKind.NotFound, ConfigurationErrors.EntryInvalid, ("reason", "entry_not_found"), ("entryId", entryId));
        }

        var type = types.Find(existing.Ref.Type);
        if (type is not null && !await authorizer.IsAllowedAsync(actor, type.PermissionFor(ConfigAccess.Edit), existing.Ref, ct).ConfigureAwait(false))
        {
            return ConfigurationErrors.Forbidden(type.PermissionFor(ConfigAccess.Edit).Value);
        }

        List<DraftEntry> entries = [.. draft.Entries.Where(e => e.EntryId != entryId)];
        return (await CommitAsync(draft, entries, null, existing.Ref, [], ct).ConfigureAwait(false)).TryGetValue(out var committed, out var failure)
            ? committed.Draft
            : failure;
    }

    public async Task<Result<Draft>> AddCollaboratorAsync(DraftId id, UserId actor, long expectedVersion, UserId collaborator, CancellationToken ct)
    {
        if (!(await OpenAsync(id, actor, expectedVersion, ct).ConfigureAwait(false)).TryGetValue(out var draft, out var error))
        {
            return error;
        }

        if (draft.Owner != actor)
        {
            return ConfigurationErrors.Forbidden("owner");
        }

        // Same permissions required: the collaborator must be allowed to edit every type the draft touches.
        foreach (var typeName in draft.Entries.Select(e => e.Ref.Type).Distinct(StringComparer.Ordinal))
        {
            if (types.Find(typeName) is { } type && !await authorizer.IsAllowedAsync(collaborator, type.PermissionFor(ConfigAccess.Edit), null, ct).ConfigureAwait(false))
            {
                return ConfigurationErrors.Forbidden(type.PermissionFor(ConfigAccess.Edit).Value);
            }
        }

        if (draft.Collaborators.Contains(collaborator) || collaborator == draft.Owner)
        {
            return draft;
        }

        var next = draft with { Collaborators = [.. draft.Collaborators, collaborator], UpdatedAt = time.GetUtcNow(), Version = draft.Version + 1 };
        return await repository.TryReplaceAsync(next, expectedVersion, null, ct).ConfigureAwait(false) ? next : await ConflictAsync(id, expectedVersion, ct).ConfigureAwait(false);
    }

    public async Task<Result> DiscardAsync(DraftId id, UserId actor, CancellationToken ct)
    {
        if (!(await VisibleAsync(id, actor, ct).ConfigureAwait(false)).TryGetValue(out var draft, out var error))
        {
            return error;
        }

        if (draft.Owner != actor)
        {
            return ConfigurationErrors.Forbidden("owner");
        }

        if (draft.Status != DraftStatus.Open)
        {
            return ConfigurationErrors.NotOpen(id.Value, draft.Status.ToString());
        }

        var next = draft with { Status = DraftStatus.Discarded, UpdatedAt = time.GetUtcNow(), Version = draft.Version + 1 };
        if (!await repository.TryReplaceAsync(next, draft.Version, null, ct).ConfigureAwait(false))
        {
            return (await ConflictAsync(id, draft.Version, ct).ConfigureAwait(false)).Error;
        }

        await secrets.DeleteAsync(id, await repository.GetSecretsAsync(id, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<DraftObjectState>> GetObjectAsync(DraftId id, UserId actor, ConfigObjectRef reference, CancellationToken ct)
    {
        if (!(await VisibleAsync(id, actor, ct).ConfigureAwait(false)).TryGetValue(out var draft, out var error))
        {
            return error;
        }

        if (types.Find(reference.Type) is not { } type)
        {
            return Error.Create(ErrorKind.Validation, ConfigurationErrors.EntryInvalid, ("reason", "unknown_type"), ("type", reference.Type));
        }

        var permission = type.PermissionFor(ConfigAccess.View);
        return await authorizer.IsAllowedAsync(actor, permission, reference, ct).ConfigureAwait(false)
            ? await new DraftView(types, draft.Entries).GetAsync(reference, ct).ConfigureAwait(false)
            : ConfigurationErrors.Forbidden(permission.Value);
    }

    public async Task<Result<Draft>> ValidateAsync(DraftId id, UserId actor, CancellationToken ct)
    {
        if (!(await VisibleAsync(id, actor, ct).ConfigureAwait(false)).TryGetValue(out var draft, out var error))
        {
            return error;
        }

        var problems = await ValidateAllAsync(draft.Entries, null, ct).ConfigureAwait(false);
        return await repository.TrySetProblemsAsync(id, draft.Version, problems, ct).ConfigureAwait(false)
            ? draft with { Problems = problems }
            : await ConflictAsync(id, draft.Version, ct).ConfigureAwait(false);
    }

    public IValidationJob StartValidation(DraftId id, UserId actor, CancellationToken ct) => new ValidationJob(this, id, actor, ct);

    public async Task<Result<ChangeSet>> BuildChangeSetAsync(DraftId id, UserId actor, CancellationToken ct)
    {
        if (!(await VisibleAsync(id, actor, ct).ConfigureAwait(false)).TryGetValue(out var draft, out var error))
        {
            return error;
        }

        if (draft.Status != DraftStatus.Open)
        {
            return ConfigurationErrors.NotOpen(id.Value, draft.Status.ToString());
        }

        var problems = await ValidateAllAsync(draft.Entries, null, ct).ConfigureAwait(false);
        await repository.TrySetProblemsAsync(id, draft.Version, problems, ct).ConfigureAwait(false);
        var errors = problems.Count(p => p.Severity == ProblemSeverity.Error);
        if (errors > 0)
        {
            return Error.Create(ErrorKind.Unprocessable, ConfigurationErrors.PublishInvalid, ("draftId", id.Value), ("errors", errors));
        }

        // Creates and updates in catalogue order, deletes after them in reverse order: referrers go before what they refer to.
        var order = types.All.Select((t, i) => (t.Type, i)).ToDictionary(x => x.Type, x => x.i, StringComparer.Ordinal);
        int Rank(DraftEntry e) => order.GetValueOrDefault(e.Ref.Type, int.MaxValue);
        var upserts = draft.Entries.Where(e => e.Operation != EntryOperation.Delete).OrderBy(Rank).ThenBy(e => e.Ref.Id);
        var deletes = draft.Entries.Where(e => e.Operation == EntryOperation.Delete).OrderByDescending(Rank).ThenBy(e => e.Ref.Id);
        return new ChangeSet(ChangeSetId.New(), id, draft.Version, [.. upserts, .. deletes]);
    }

    /// <summary>Creates a draft (hidden for quick edits).</summary>
    public async Task<Result<Draft>> CreateCoreAsync(LocalizedName name, UserId owner, bool hidden, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var draft = new Draft(DraftId.New(), name, owner, [], DraftStatus.Open, [], [], now, now, null, null, 0);
        await repository.AddAsync(draft, hidden, ct).ConfigureAwait(false);
        return draft;
    }

    /// <summary>Validates all entries, plus what only the whole draft decides, reporting how many entries are done.</summary>
    public async Task<IReadOnlyList<Problem>> ValidateAllAsync(IReadOnlyList<DraftEntry> entries, Action<int>? progress, CancellationToken ct)
    {
        var view = new DraftView(types, entries);
        List<Problem> problems = [];
        var done = 0;
        foreach (var chunk in entries.Chunk(Math.Max(1, settings.ValidationChunkSize)))
        {
            problems.AddRange(await validator.ValidateAsync(view, chunk, ct).ConfigureAwait(false));
            done += chunk.Length;
            progress?.Invoke(done);
        }

        problems.AddRange(await validator.ValidateWholeAsync(view, ct).ConfigureAwait(false));
        return DraftValidator.Sort(problems);
    }

    /// <summary>Stores the problems of a validation that ran in the background; they are dropped when the draft changed meanwhile.</summary>
    public async Task<Result<Draft>> StoreProblemsAsync(Draft draft, IReadOnlyList<Problem> problems, CancellationToken ct) =>
        await repository.TrySetProblemsAsync(draft.Id, draft.Version, problems, ct).ConfigureAwait(false)
            ? draft with { Problems = problems }
            : await ConflictAsync(draft.Id, draft.Version, ct).ConfigureAwait(false);

    /// <summary>Marks a draft as waiting for approval (quick edit of a type that needs it).</summary>
    public async Task<Result<Draft>> RequestApprovalAsync(Draft draft, UserId actor, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var next = draft with { Status = DraftStatus.AwaitingApproval, Approval = new ApprovalInfo(actor, now), UpdatedAt = now, Version = draft.Version + 1 };
        return await repository.TryReplaceAsync(next, draft.Version, null, ct).ConfigureAwait(false) ? next : await ConflictAsync(draft.Id, draft.Version, ct).ConfigureAwait(false);
    }

    private static (List<DraftEntry> Entries, DraftEntry? Entry) Merge(IReadOnlyList<DraftEntry> current, DraftEntry? existing, Guid entryId, EntryRequest request, JsonElement? content)
    {
        List<DraftEntry> entries = [.. current.Where(e => e.EntryId != entryId)];
        var operation = request.Operation;
        long? baseVersion = request.BaseVersion;
        if (existing is not null)
        {
            // A replaced entry keeps its base version. A create that is deleted again cancels out; a delete followed by a create is an update.
            baseVersion = existing.BaseVersion;
            if (existing.Operation == EntryOperation.Create && operation == EntryOperation.Delete)
            {
                return (entries, null);
            }

            if (existing.Operation == EntryOperation.Delete && operation == EntryOperation.Create)
            {
                operation = EntryOperation.Update;
            }
            else if (existing.Operation == EntryOperation.Create && operation == EntryOperation.Update)
            {
                operation = EntryOperation.Create;
            }
        }

        var entry = new DraftEntry(entryId, request.Ref, operation, operation == EntryOperation.Create ? null : baseVersion, content, default);
        entries.Add(entry);
        return (entries, entry);
    }

    private async Task<Result<EntryResult>> CommitAsync(
        Draft draft, List<DraftEntry> entries, DraftEntry? entry, ConfigObjectRef changed, IReadOnlyList<DraftSecretRef> created, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (entry is not null)
        {
            entry = entry with { UpdatedAt = now };
            entries[entries.FindIndex(e => e.EntryId == entry.EntryId)] = entry;
        }

        // The changed entry and the entries that refer to it are checked again; the problems of the others stay.
        var view = new DraftView(types, entries);
        HashSet<ConfigObjectRef> recheck = [changed];

        // What a delete entry is blocked by depends on every other entry, so deletes are always checked again.
        recheck.UnionWith(entries.Where(e => e.Operation == EntryOperation.Delete).Select(e => e.Ref));
        foreach (var other in entries)
        {
            if (other.Content is { } content && types.Find(other.Ref.Type)?.GetReferences(content).Contains(changed) == true)
            {
                recheck.Add(other.Ref);
            }
        }

        var fresh = await validator.ValidateAsync(view, entries.Where(e => recheck.Contains(e.Ref)), ct).ConfigureAwait(false);
        var whole = await validator.ValidateWholeAsync(view, ct).ConfigureAwait(false);
        var present = entries.Select(e => e.Ref).ToHashSet();
        var kept = draft.Problems.Where(p => p.Object is { } o && present.Contains(o) && !recheck.Contains(o));
        var problems = DraftValidator.Sort([.. kept, .. fresh, .. whole]);

        // The vault references the draft holds after the change: those of the entries that stay, plus the new ones.
        var oldSecrets = await repository.GetSecretsAsync(draft.Id, ct).ConfigureAwait(false);
        var keptIds = entries.Where(e => e.Content is not null).SelectMany(e => DraftSecrets.ReferencesIn(e.Content!.Value)).ToHashSet();
        var secretRefs = oldSecrets.Where(s => keptIds.Contains(s.Secret)).Concat(created).ToList();

        var next = draft with { Entries = entries, Problems = problems, UpdatedAt = now, Version = draft.Version + 1 };
        if (!await repository.TryReplaceAsync(next, draft.Version, secretRefs, ct).ConfigureAwait(false))
        {
            return (await ConflictAsync(draft.Id, draft.Version, ct).ConfigureAwait(false)).Error;
        }

        await secrets.DeleteAsync(draft.Id, oldSecrets.Where(s => !keptIds.Contains(s.Secret)), ct).ConfigureAwait(false);
        return new EntryResult(next, entry, [.. problems.Where(p => p.Object is { } o && recheck.Contains(o))]);
    }

    private async Task<Result<Draft>> VisibleAsync(DraftId id, UserId actor, CancellationToken ct)
    {
        var draft = await repository.GetAsync(id, ct).ConfigureAwait(false);
        return draft is not null && (draft.Owner == actor || draft.Collaborators.Contains(actor)) && draft.Status != DraftStatus.Discarded
            ? draft
            : ConfigurationErrors.NotFound(id.Value);
    }

    private async Task<Result<Draft>> OpenAsync(DraftId id, UserId actor, long expectedVersion, CancellationToken ct)
    {
        if (!(await VisibleAsync(id, actor, ct).ConfigureAwait(false)).TryGetValue(out var draft, out var error))
        {
            return error;
        }

        if (draft.Status != DraftStatus.Open)
        {
            return ConfigurationErrors.NotOpen(id.Value, draft.Status.ToString());
        }

        return draft.Version == expectedVersion ? draft : ConfigurationErrors.VersionConflict(expectedVersion, draft.Version);
    }

    private async Task<Result<Draft>> ConflictAsync(DraftId id, long expected, CancellationToken ct)
    {
        var actual = (await repository.GetAsync(id, ct).ConfigureAwait(false))?.Version ?? -1;
        return ConfigurationErrors.VersionConflict(expected, actual);
    }
}
