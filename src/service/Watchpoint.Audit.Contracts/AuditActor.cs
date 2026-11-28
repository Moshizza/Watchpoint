namespace Watchpoint.Audit.Contracts;

/// <summary>Who performed an audited action.</summary>
/// <param name="Kind">The kind of actor.</param>
/// <param name="Id">The actor id (user id…), when it has one.</param>
/// <param name="DisplayName">A display name for logs; personal names are not stored in audit rows, see AUD §13.</param>
/// <param name="Client">The client software.</param>
/// <param name="Workstation">The workstation name.</param>
/// <param name="IpAddress">The remote address.</param>
/// <param name="Via">How the actor reached the node, e.g. <c>HQ via federation link</c>.</param>
public sealed record AuditActor(ActorKind Kind, Guid? Id, string DisplayName, AuditClientKind? Client, string? Workstation, string? IpAddress, string? Via)
{
    /// <summary>The system acting on its own (scheduler, retention, start-up).</summary>
    /// <param name="name">The component, e.g. <c>scheduler</c>.</param>
    /// <returns>The actor.</returns>
    public static AuditActor System(string name) => new(ActorKind.System, null, name, null, null, null, null);

    /// <summary>A signed-in user.</summary>
    /// <param name="id">The user id.</param>
    /// <param name="displayName">The display name.</param>
    /// <param name="client">The client software.</param>
    /// <param name="workstation">The workstation.</param>
    /// <param name="ipAddress">The remote address.</param>
    /// <returns>The actor.</returns>
    public static AuditActor User(Guid id, string displayName, AuditClientKind? client = null, string? workstation = null, string? ipAddress = null) =>
        new(ActorKind.User, id, displayName, client, workstation, ipAddress, null);

    /// <summary>A user of another site acting through a federation link.</summary>
    /// <param name="id">The remote user id.</param>
    /// <param name="displayName">The display name.</param>
    /// <param name="via">The link, e.g. <c>HQ via federation link</c>.</param>
    /// <returns>The actor.</returns>
    public static AuditActor Federated(Guid id, string displayName, string via) => new(ActorKind.Federation, id, displayName, null, null, null, via);
}
