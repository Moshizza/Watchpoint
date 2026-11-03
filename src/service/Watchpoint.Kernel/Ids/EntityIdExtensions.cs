namespace Watchpoint.Kernel.Ids;

/// <summary>Helpers for UUID-based ids.</summary>
public static class EntityIdExtensions
{
    /// <summary>Formats an id for logs as <c>"{Prefix}:{Value}"</c>, e.g. <c>dev:0192f3a4-…</c>.</summary>
    /// <typeparam name="TId">The id type.</typeparam>
    /// <param name="id">The id.</param>
    /// <returns>The log representation.</returns>
    public static string ToLogString<TId>(this TId id)
        where TId : struct, IEntityId<TId> =>
        TId.Prefix + ":" + EntityIdHelper.Format(id.Value);
}
