using System.Diagnostics.CodeAnalysis;

namespace Watchpoint.State.Contracts;

/// <summary>Reads the overall-state counters maintained per location, server, tag and site (spec STA §5, §6.4).</summary>
public interface IAggregateReader
{
    /// <summary>Gets the counts of one scope (zero counts when the scope is empty or unknown).</summary>
    /// <param name="scope">The scope.</param>
    /// <returns>The counts.</returns>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Name fixed by spec STA §5.")]
    AggregateCounts Get(AggregateScope scope);

    /// <summary>Gets the counts of several scopes.</summary>
    /// <param name="scopes">The scopes.</param>
    /// <returns>The counts, keyed by scope.</returns>
    IReadOnlyDictionary<AggregateScope, AggregateCounts> GetMany(IReadOnlyCollection<AggregateScope> scopes);
}
