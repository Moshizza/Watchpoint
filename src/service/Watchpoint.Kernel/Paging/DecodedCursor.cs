using System.Text.Json;

namespace Watchpoint.Kernel.Paging;

/// <summary>A decoded paging cursor.</summary>
/// <param name="KeyValues">The sort key values of the last item of the previous page, in sort order.</param>
/// <param name="LastId">The id of the last item of the previous page (tie-breaker).</param>
public sealed record DecodedCursor(IReadOnlyList<JsonElement> KeyValues, Guid LastId);
