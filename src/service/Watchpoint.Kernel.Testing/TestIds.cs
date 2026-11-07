using Watchpoint.Kernel.Ids;

namespace Watchpoint.Kernel.Testing;

/// <summary>
/// Deterministic ids for tests: the same number always gives the same id, and ids sort by number. The UUIDs are
/// version 7 with the number in the last 12 hex digits, e.g. <c>TestIds.Uuid(1)</c> =
/// <c>00000000-0000-7000-8000-000000000001</c>.
/// </summary>
public static class TestIds
{
    /// <summary>Returns the deterministic UUID for a number.</summary>
    /// <param name="n">A positive number.</param>
    /// <returns>The UUID.</returns>
    public static Guid Uuid(long n)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(n, 0xFFFF_FFFF_FFFFL);
        return System.Guid.Parse(FormattableString.Invariant($"00000000-0000-7000-8000-{n:x12}"));
    }

    /// <summary>Returns the deterministic id of any UUID-based type for a number.</summary>
    /// <typeparam name="TId">The id type, e.g. <see cref="DeviceId"/>.</typeparam>
    /// <param name="n">A positive number.</param>
    /// <returns>The id.</returns>
    public static TId Of<TId>(long n)
        where TId : struct, IEntityId<TId> => TId.From(Uuid(n));

    /// <summary>Returns the deterministic device id for a number.</summary>
    /// <param name="n">A positive number.</param>
    /// <returns>The id.</returns>
    public static DeviceId Device(long n) => Of<DeviceId>(n);

    /// <summary>Returns the deterministic server id for a number.</summary>
    /// <param name="n">A positive number.</param>
    /// <returns>The id.</returns>
    public static ServerId Server(long n) => Of<ServerId>(n);

    /// <summary>Returns the deterministic site id for a number.</summary>
    /// <param name="n">A positive number.</param>
    /// <returns>The id.</returns>
    public static SiteId Site(long n) => Of<SiteId>(n);

    /// <summary>Returns the deterministic user id for a number.</summary>
    /// <param name="n">A positive number.</param>
    /// <returns>The id.</returns>
    public static UserId User(long n) => Of<UserId>(n);
}
