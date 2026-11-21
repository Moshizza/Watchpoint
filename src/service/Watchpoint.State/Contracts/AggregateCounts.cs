using Watchpoint.Model.Contracts;

namespace Watchpoint.State.Contracts;

/// <summary>The entity counts of one <see cref="AggregateScope"/> (spec STA §5).</summary>
/// <param name="Total">The total number of entities in the scope.</param>
/// <param name="ByState">The count per overall state; states with zero entities are omitted.</param>
public sealed record AggregateCounts(int Total, IReadOnlyDictionary<OverallState, int> ByState);
