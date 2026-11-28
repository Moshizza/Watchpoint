namespace Watchpoint.Audit.Contracts;

/// <summary>Resolves labels when audit entries are read: audit rows keep ids only, so a privacy erase never touches them (spec AUD §13).</summary>
public interface IAuditLabelResolver
{
    /// <summary>Resolves the labels of objects.</summary>
    /// <param name="objects">Objects with an id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Labels by <c>(type, id)</c>; unknown objects are absent.</returns>
    ValueTask<IReadOnlyDictionary<(string Type, Guid Id), string>> ResolveAsync(IReadOnlyCollection<(string Type, Guid Id)> objects, CancellationToken ct);

    /// <summary>The localised label of an action (translation key <c>audit.action.&lt;module&gt;.&lt;verb&gt;</c>).</summary>
    /// <param name="action">The action.</param>
    /// <param name="language">The language tag, e.g. <c>fr</c>.</param>
    /// <returns>The label.</returns>
    string ActionLabel(AuditAction action, string language);
}
