namespace Watchpoint.Kernel.Paging;

/// <summary>One page of a keyset-paged list.</summary>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="Items">The items of the page.</param>
/// <param name="NextCursor">The cursor of the next page, or <see langword="null"/> on the last page.</param>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
