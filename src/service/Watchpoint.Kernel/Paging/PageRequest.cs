using Watchpoint.Kernel.Results;

namespace Watchpoint.Kernel.Paging;

/// <summary>A request for one page of a keyset-paged list.</summary>
/// <param name="Limit">The maximum number of items (1 to <see cref="MaxLimit"/>).</param>
/// <param name="Cursor">The opaque cursor returned with the previous page, or <see langword="null"/> for the first page.</param>
public sealed record PageRequest(int Limit = PageRequest.DefaultLimit, string? Cursor = null)
{
    /// <summary>The limit used when none is given.</summary>
    public const int DefaultLimit = 100;

    /// <summary>The largest allowed limit.</summary>
    public const int MaxLimit = 1000;

    /// <summary>Validates a page request from client input.</summary>
    /// <param name="limit">The limit; <see langword="null"/> means <see cref="DefaultLimit"/>.</param>
    /// <param name="cursor">The cursor; <see langword="null"/> or empty means the first page.</param>
    /// <returns>The request, or <c>common.value.out_of_range</c> (arguments <c>min</c> 1, <c>max</c> 1000).</returns>
    public static Result<PageRequest> Create(int? limit, string? cursor)
    {
        var value = limit ?? DefaultLimit;
        if (value is < 1 or > MaxLimit)
        {
            return CommonErrors.OutOfRange(1, MaxLimit);
        }

        return new PageRequest(value, string.IsNullOrEmpty(cursor) ? null : cursor);
    }
}
