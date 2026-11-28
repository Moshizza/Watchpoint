using System.Runtime.CompilerServices;
using System.Text.Json;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Rights.Contracts;
using Watchpoint.Rights.Internal.Catalogue;
using Watchpoint.Rights.Internal.Evaluation;

namespace Watchpoint.Rights.Internal.Configuration;

/// <summary>
/// The configuration object type <c>rights.grants</c> (spec RGT §5): one document per user (id = user id) with all its grants.
/// <see cref="ValidateAsync"/> reports problems in plain language keys; <see cref="ApplyAsync"/> is what the CFG module adapter
/// calls on publish (after T3.02: <c>PrepareApply</c>/<c>CompleteApply</c>).
/// </summary>
internal sealed class GrantsObjectType(IGrantStore store, IPermissionCatalogue catalogue, Func<string, bool> calendarExists) : IConfigurableObjectType
{
    public const string TypeName = "rights.grants";

    public string Type => TypeName;

    public string Module => "Rights";

    public bool SupportsSoftLock => false;

    public bool Tabular => false;

    public JsonElement? Schema => null;

    public PermissionCode PermissionFor(ConfigAccess access) => PermissionCode.FromTrusted(access switch
    {
        ConfigAccess.View => "config.rights.view",
        ConfigAccess.Edit => "config.rights.edit",
        _ => "config.publish",
    });

    public Task<ConfigDocument?> LoadAsync(Guid id, CancellationToken ct)
    {
        var grants = store.GetGrants(UserId.From(id));
        return Task.FromResult<ConfigDocument?>(grants.Count == 0 ? null : new ConfigDocument(new ConfigObjectRef(TypeName, id), grants.Count, ToContent(grants)));
    }

    public async IAsyncEnumerable<ConfigDocument> ListAsync(ConfigListQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public ValueTask<IReadOnlyList<Problem>> ValidateAsync(DraftEntry entry, IDraftView draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var problems = new List<Problem>();
        if (entry.Content is not { } content || !TryParse(content, UserId.From(entry.Ref.Id), out var grants, out var parseError))
        {
            problems.Add(Fail("rights.grant.invalid_document", entry, ("reason", entry.Content is null ? "missing" : "unreadable")));
            return ValueTask.FromResult<IReadOnlyList<Problem>>(problems);
        }

        _ = parseError;
        foreach (var error in new GrantValidator(new PermissionCatalogueAccess(catalogue)).Validate(grants))
        {
            problems.Add(Fail(error.Code.Value, entry, [.. error.Args.Select(a => (a.Key, a.Value))]));
        }

        foreach (var g in grants.Where(g => g.ScheduleCalendar is { } c && !calendarExists(c)))
        {
            problems.Add(Fail("rights.grant.calendar_unknown", entry, ("calendar", g.ScheduleCalendar)));
        }

        return ValueTask.FromResult<IReadOnlyList<Problem>>(problems);
    }

    public IReadOnlyList<ConfigObjectRef> GetReferences(JsonElement content) => [];

    /// <summary>Applies a validated document: replaces the user's grants.</summary>
    /// <param name="user">The user.</param>
    /// <param name="content">The document.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ApplyAsync(UserId user, JsonElement content, CancellationToken ct)
    {
        if (TryParse(content, user, out var grants, out _))
        {
            await store.ReplaceAsync(user, grants, ct).ConfigureAwait(false);
        }
    }

    internal static JsonElement ToContent(IReadOnlyList<RightGrant> grants)
    {
        var dto = new GrantsDocument([.. grants.Select(GrantDto.Of)]);
        return JsonSerializer.SerializeToElement(dto, GrantDocumentContext.Default.GrantsDocument);
    }

    internal static bool TryParse(JsonElement content, UserId user, out IReadOnlyList<RightGrant> grants, out string? error)
    {
        grants = [];
        error = null;
        try
        {
            var doc = content.Deserialize(GrantDocumentContext.Default.GrantsDocument);
            if (doc?.Grants is null)
            {
                error = "missing";
                return false;
            }

            grants = [.. doc.Grants.Select(g => g.ToGrant(user))];
            return true;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException)
        {
            error = e.Message;
            return false;
        }
    }

    private static Problem Fail(string code, DraftEntry entry, params (string Key, object? Value)[] args) =>
        new(ProblemSeverity.Error, code, args.ToDictionary(a => a.Key, a => a.Value), entry.Ref, null, []);
}
