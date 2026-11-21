using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Watchpoint.Model.Contracts;
using Watchpoint.State.Contracts;

namespace Watchpoint.State.Internal;

/// <summary>
/// The overall-state counters per scope (spec STA §6.4): one <c>int[]</c> indexed by <see cref="OverallState"/> per
/// scope, mutated with <see cref="Interlocked"/> so no lock is needed even though many partitions update different
/// scopes concurrently. A background job recomputes the counters from scratch and corrects drift (STA-U-022, 023).
/// </summary>
internal sealed class AggregateCounters : IAggregateReader
{
    private static readonly int _stateCount = Enum.GetValues<OverallState>().Length;

    private ConcurrentDictionary<AggregateScope, int[]> _counts = new();

    /// <summary>Increments the counter of a scope and state by one.</summary>
    public void Increment(AggregateScope scope, OverallState state) => Mutate(scope, state, 1);

    /// <summary>Decrements the counter of a scope and state by one.</summary>
    public void Decrement(AggregateScope scope, OverallState state) => Mutate(scope, state, -1);

    /// <inheritdoc />
    public AggregateCounts Get(AggregateScope scope) => Build(_counts, scope);

    /// <inheritdoc />
    public IReadOnlyDictionary<AggregateScope, AggregateCounts> GetMany(IReadOnlyCollection<AggregateScope> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var snapshot = _counts;
        var result = new Dictionary<AggregateScope, AggregateCounts>(scopes.Count);
        foreach (var scope in scopes)
        {
            result[scope] = Build(snapshot, scope);
        }

        return result;
    }

    /// <summary>
    /// Recomputes every counter from <paramref name="entities"/> (the current state of every owned partition), compares
    /// it with the live counters and replaces them if they drifted (spec STA §6.4). Never expected to find a
    /// difference; a difference is a safety-net correction, logged at <see cref="LogLevel.Error"/> (event 1730).
    /// </summary>
    /// <param name="entities">Every currently tracked entity with its membership scopes and overall state.</param>
    /// <param name="logger">The logger for a drift correction.</param>
    /// <returns><see langword="true"/> when a drift was found and corrected.</returns>
    public bool RunConsistencyCheck(IEnumerable<(IReadOnlyList<AggregateScope> Scopes, OverallState State)> entities, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(logger);
        var fresh = new ConcurrentDictionary<AggregateScope, int[]>();
        foreach (var (scopes, state) in entities)
        {
            foreach (var scope in scopes)
            {
                fresh.GetOrAdd(scope, static _ => new int[_stateCount])[(int)state]++;
            }
        }

        var current = _counts;
        var drifted = Differs(current, fresh);
        if (drifted)
        {
            StateLog.AggregateDriftCorrected(logger, "recomputed from scratch");
            Interlocked.Exchange(ref _counts, fresh);
        }

        return drifted;
    }

    private static bool Differs(ConcurrentDictionary<AggregateScope, int[]> a, ConcurrentDictionary<AggregateScope, int[]> b)
    {
        if (a.Count != b.Count)
        {
            return true;
        }

        foreach (var (scope, counts) in a)
        {
            if (!b.TryGetValue(scope, out var other) || !counts.AsSpan().SequenceEqual(other))
            {
                return true;
            }
        }

        return false;
    }

    private void Mutate(AggregateScope scope, OverallState state, int delta)
    {
        var counts = _counts.GetOrAdd(scope, static _ => new int[_stateCount]);
        Interlocked.Add(ref counts[(int)state], delta);
    }

    private static AggregateCounts Build(ConcurrentDictionary<AggregateScope, int[]> source, AggregateScope scope)
    {
        if (!source.TryGetValue(scope, out var counts))
        {
            return new AggregateCounts(0, EmptyByState);
        }

        var snapshot = (int[])counts.Clone();
        var byState = new Dictionary<OverallState, int>();
        var total = 0;
        for (var i = 0; i < snapshot.Length; i++)
        {
            total += snapshot[i];
            if (snapshot[i] != 0)
            {
                byState[(OverallState)i] = snapshot[i];
            }
        }

        return new AggregateCounts(total, byState);
    }

    private static readonly IReadOnlyDictionary<OverallState, int> EmptyByState = new Dictionary<OverallState, int>();
}
