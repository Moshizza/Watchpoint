namespace Watchpoint.Kernel.Paging;

/// <summary>One field of a sort order.</summary>
/// <param name="Name">The field name, as exposed by the API (e.g. <c>createdAt</c>).</param>
/// <param name="Descending"><see langword="true"/> for descending order.</param>
public sealed record SortField(string Name, bool Descending);
