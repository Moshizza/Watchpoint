namespace Watchpoint.Kernel.Time;

/// <summary>
/// How <see cref="SiteTimeZone.ToUtc"/> resolves a local time that does not exist (daylight-saving gap) or exists
/// twice (daylight-saving overlap).
/// </summary>
public enum AmbiguityPolicy
{
    /// <summary>
    /// Overlap: the first occurrence (offset before the change). Gap: the earlier instant, i.e. the local time is
    /// read with the offset after the gap (Paris 02:30 on the spring change → 00:30Z).
    /// </summary>
    EarlierOffset,

    /// <summary>
    /// Overlap: the second occurrence (offset after the change). Gap: the later instant, i.e. the local time is
    /// shifted forward by the length of the gap (Paris 02:30 on the spring change → 03:30 local, 01:30Z).
    /// </summary>
    LaterOffset,

    /// <summary>Gaps and overlaps fail with <c>common.time.invalid_local</c>.</summary>
    Reject,
}
