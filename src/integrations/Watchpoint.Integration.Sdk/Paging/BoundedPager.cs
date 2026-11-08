namespace Watchpoint.Integration.Sdk.Paging;

/// <summary>
/// Thrown when pagination detects an identical cursor or infinite paging loop (INT §10).
/// </summary>
public sealed class PagingLoopDetectedException : Exception
{
    /// <summary>Initializes a new instance of <see cref="PagingLoopDetectedException"/>.</summary>
    public PagingLoopDetectedException() : base("Paging loop detected: repeated cursor returned by vendor API.") { }

    /// <summary>Initializes a new instance of <see cref="PagingLoopDetectedException"/> with a message.</summary>
    /// <param name="message">The exception message.</param>
    public PagingLoopDetectedException(string message) : base(message) { }

    /// <summary>Initializes a new instance of <see cref="PagingLoopDetectedException"/> with a message and inner exception.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="inner">The inner exception.</param>
    public PagingLoopDetectedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Result of fetching a single page from a paged resource.
/// </summary>
/// <typeparam name="T">Type of items in the page.</typeparam>
/// <param name="Items">Items contained in the page.</param>
/// <param name="NextCursor">Cursor for fetching next page, if any.</param>
public sealed record PageResult<T>(IReadOnlyList<T> Items, string? NextCursor = null);

/// <summary>
/// Safe pager executing paged requests with loop detection, page size bounds, and max pages limit (INT §10).
/// </summary>
public static class BoundedPager
{
    /// <summary>Default maximum number of pages to fetch (10,000).</summary>
    public const int DefaultMaxPages = 10_000;

    /// <summary>Default delay when an empty page is encountered (1 second).</summary>
    public static readonly TimeSpan DefaultEmptyPageDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Reads pages until no more items, applying safety limits for cursor loops, max pages, and empty page delays.
    /// </summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <param name="fetchPage">Function taking (cursor, cancellationToken) and returning a page.</param>
    /// <param name="expectedPageSize">The expected page size; shorter page indicates completion.</param>
    /// <param name="time">Time provider for empty page delays.</param>
    /// <param name="maxPages">Maximum allowed pages (default 10,000).</param>
    /// <param name="emptyPageDelay">Delay when an empty page is returned (default 1s).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Asynchronous stream of items.</returns>
    public static async IAsyncEnumerable<T> ReadAllAsync<T>(
        Func<string?, CancellationToken, ValueTask<PageResult<T>>> fetchPage,
        int expectedPageSize,
        TimeProvider time,
        int maxPages = DefaultMaxPages,
        TimeSpan? emptyPageDelay = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fetchPage);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPages);

        var delay = emptyPageDelay ?? DefaultEmptyPageDelay;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? currentCursor = null;
        var pageCount = 0;

        while (!ct.IsCancellationRequested)
        {
            if (pageCount >= maxPages)
            {
                yield break;
            }

            pageCount++;
            var page = await fetchPage(currentCursor, ct).ConfigureAwait(false);
            var items = page.Items;

            if (items.Count == 0)
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, time, ct).ConfigureAwait(false);
                }
                yield break;
            }

            foreach (var item in items)
            {
                yield return item;
            }

            // Stop when page is shorter than expected page size
            if (items.Count < expectedPageSize)
            {
                yield break;
            }

            var nextCursor = page.NextCursor;
            if (string.IsNullOrEmpty(nextCursor))
            {
                yield break;
            }

            // Cycle detection: cursor already seen or identical to current
            if (string.Equals(nextCursor, currentCursor, StringComparison.Ordinal) || !seenCursors.Add(nextCursor))
            {
                throw new PagingLoopDetectedException($"Repeated cursor detected: '{nextCursor}'.");
            }

            currentCursor = nextCursor;
        }
    }
}
