using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;

namespace Watchpoint.Configuration.Contracts;

/// <summary>A configurable object: its type (<c>registry.device</c>) and id (spec CFG §4).</summary>
/// <param name="Type">The object type, <c>&lt;module&gt;.&lt;object&gt;</c>.</param>
/// <param name="Id">The object id.</param>
public readonly record struct ConfigObjectRef(string Type, Guid Id)
{
    /// <summary>Returns <c>type:id</c>.</summary>
    /// <returns>The text form.</returns>
    public override string ToString() => $"{Type}:{Id:D}";
}

/// <summary>A live object as a module's JSON document.</summary>
/// <param name="Ref">The object.</param>
/// <param name="Version">The object's version.</param>
/// <param name="Content">The document.</param>
public sealed record ConfigDocument(ConfigObjectRef Ref, long Version, JsonElement Content);

/// <summary>What a draft entry does to its object.</summary>
public enum EntryOperation
{
    /// <summary>Creates the object.</summary>
    Create,

    /// <summary>Replaces the document of the object.</summary>
    Update,

    /// <summary>Deletes the object.</summary>
    Delete,
}

/// <summary>The severity of a problem.</summary>
public enum ProblemSeverity
{
    /// <summary>Blocks publishing.</summary>
    Error,

    /// <summary>Does not block publishing.</summary>
    Warning,
}

/// <summary>A suggested fix of a problem: a JSON Patch the editor can apply to the entry.</summary>
/// <param name="Code">The fix code, e.g. <c>config.fix.set_default</c> (translated in the UI).</param>
/// <param name="Args">The fix arguments.</param>
/// <param name="Patch">The JSON Patch (RFC 6902: <c>add</c>, <c>remove</c>, <c>replace</c>), or <see langword="null"/> when the fix is manual.</param>
public sealed record FixSuggestion(string Code, IReadOnlyDictionary<string, object?> Args, JsonElement? Patch)
{
    /// <inheritdoc />
    public bool Equals(FixSuggestion? other) =>
        other is not null && Code == other.Code && ProblemValues.Equal(Args, other.Args) && ProblemValues.Equal(Patch?.GetRawText(), other.Patch?.GetRawText());

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Code, Args.Count);
}

/// <summary>
/// A validation problem: data only, never a message (spec CFG §5, API §5.4). The UI translates <c>errors.&lt;Code&gt;</c> with
/// <see cref="Args"/>.
/// </summary>
/// <param name="Severity">The severity.</param>
/// <param name="Code">The problem code, e.g. <c>config.reference.missing</c> or <c>common.value.required</c>.</param>
/// <param name="Args">The arguments of the message.</param>
/// <param name="Object">The object the problem is about, if any.</param>
/// <param name="Path">The JSON pointer of the field in the document, if any.</param>
/// <param name="Fixes">The suggested fixes.</param>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Member name fixed by spec CFG §5.")]
public sealed record Problem(
    ProblemSeverity Severity,
    string Code,
    IReadOnlyDictionary<string, object?> Args,
    ConfigObjectRef? Object,
    string? Path,
    IReadOnlyList<FixSuggestion> Fixes)
{
    /// <summary>Problems are data: two problems are equal when code, arguments (by value), object, path and fixes are.</summary>
    /// <param name="other">The other problem.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public bool Equals(Problem? other) =>
        other is not null && Severity == other.Severity && Code == other.Code && Object == other.Object && Path == other.Path
        && ProblemValues.Equal(Args, other.Args) && Fixes.SequenceEqual(other.Fixes);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Severity, Code, Object, Path);
}

/// <summary>Value equality of problem arguments (dictionaries and lists compare by content).</summary>
internal static class ProblemValues
{
    public static bool Equal(object? a, object? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        if (a is System.Collections.IDictionary || b is System.Collections.IDictionary || (a.GetType().IsGenericType && a.GetType().GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))))
        {
            var left = ((System.Collections.IEnumerable)a).Cast<object>().Select(Pair).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
            var right = ((System.Collections.IEnumerable)b).Cast<object>().Select(Pair).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
            return left.Count == right.Count && left.Zip(right).All(x => x.First.Key == x.Second.Key && Equal(x.First.Value, x.Second.Value));
        }

        if (a is not string && b is not string && a is System.Collections.IEnumerable ea && b is System.Collections.IEnumerable eb)
        {
            var left = ea.Cast<object?>().ToList();
            var right = eb.Cast<object?>().ToList();
            return left.Count == right.Count && left.Zip(right).All(x => Equal(x.First, x.Second));
        }

        return a.Equals(b);
    }

    private static (string Key, object? Value) Pair(object entry)
    {
        var type = entry.GetType();
        return ((string)type.GetProperty("Key")!.GetValue(entry)!, type.GetProperty("Value")!.GetValue(entry));
    }
}

/// <summary>One change of a draft.</summary>
/// <param name="EntryId">The entry id.</param>
/// <param name="Ref">The object.</param>
/// <param name="Operation">What is done to it.</param>
/// <param name="BaseVersion">The version of the object the edit started from; <see langword="null"/> for a create.</param>
/// <param name="Content">The full new document; <see langword="null"/> for a delete.</param>
/// <param name="UpdatedAt">When the entry last changed.</param>
public sealed record DraftEntry(Guid EntryId, ConfigObjectRef Ref, EntryOperation Operation, long? BaseVersion, JsonElement? Content, DateTimeOffset UpdatedAt);

/// <summary>The life cycle of a draft.</summary>
public enum DraftStatus
{
    /// <summary>Being edited.</summary>
    Open,

    /// <summary>Waiting for an approver.</summary>
    AwaitingApproval,

    /// <summary>Waiting for its publish time.</summary>
    Scheduled,

    /// <summary>Being published.</summary>
    Publishing,

    /// <summary>Published.</summary>
    Published,

    /// <summary>Thrown away.</summary>
    Discarded,

    /// <summary>Publishing failed.</summary>
    Failed,
}

/// <summary>Who asked for approval and when (T3.02 extends it).</summary>
/// <param name="RequestedBy">The requester.</param>
/// <param name="RequestedAt">The time.</param>
public sealed record ApprovalInfo(UserId RequestedBy, DateTimeOffset RequestedAt);

/// <summary>
/// A draft: a set of changes to configuration objects kept apart from the live configuration until it is published
/// (spec CFG §5). Every change of the draft increases <see cref="Version"/>, which editors send back to detect a concurrent edit.
/// </summary>
/// <param name="Id">The draft id.</param>
/// <param name="Name">The name.</param>
/// <param name="Owner">The owner.</param>
/// <param name="Collaborators">The other users who may edit it.</param>
/// <param name="Status">The status.</param>
/// <param name="Entries">The entries.</param>
/// <param name="Problems">The problems found by the last validation.</param>
/// <param name="CreatedAt">When created.</param>
/// <param name="UpdatedAt">When last changed.</param>
/// <param name="PublishAt">The scheduled publish time (T3.02).</param>
/// <param name="Approval">The approval request (T3.02).</param>
/// <param name="Version">The draft version.</param>
public sealed record Draft(
    DraftId Id,
    LocalizedName Name,
    UserId Owner,
    IReadOnlyList<UserId> Collaborators,
    DraftStatus Status,
    IReadOnlyList<DraftEntry> Entries,
    IReadOnlyList<Problem> Problems,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishAt,
    ApprovalInfo? Approval,
    long Version);

/// <summary>
/// A change set: the entries of a draft frozen for publishing (spec CFG §7). Nothing in it has touched the live configuration;
/// the modules apply it with <see cref="Origin"/> in their change context (REG §6: origin <c>publish:&lt;changeSetId&gt;</c>).
/// </summary>
/// <param name="Id">The change set id.</param>
/// <param name="Draft">The draft it was made from.</param>
/// <param name="DraftVersion">The draft version it was made from.</param>
/// <param name="Entries">The entries, in the order they must be applied (type order of the catalogue, then object id).</param>
public sealed record ChangeSet(ChangeSetId Id, DraftId Draft, long DraftVersion, IReadOnlyList<DraftEntry> Entries)
{
    /// <summary>Gets the origin text modules record: <c>publish:&lt;changeSetId&gt;</c>.</summary>
    public string Origin => "publish:" + Id.Value.ToString("D");
}
