namespace Watchpoint.Audit.Contracts;

/// <summary>An object an audited action affected.</summary>
/// <param name="Type">The object type: <c>device</c>, <c>user</c>, <c>rights.grants</c>, <c>connection</c>…</param>
/// <param name="Id">The object id.</param>
/// <param name="Label">A label for the log sink only; the audit store keeps ids and resolves labels when it queries (AUD §13).</param>
public sealed record AuditObjectRef(string Type, Guid? Id, string? Label = null);
