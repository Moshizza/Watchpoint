using Watchpoint.History.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.History.Internal.Queries;

/// <summary>Turns the caller's visible scope into one SQL restriction (spec HIS §7): locations or devices, whichever list is smaller.</summary>
internal static class ScopeSelector
{
    /// <summary>Selects the restriction.</summary>
    /// <param name="scope">The scope; <see langword="null"/> means unrestricted.</param>
    /// <param name="nothingVisible">Set when the chosen list is empty: the query has no result and is not run.</param>
    /// <returns>The clause.</returns>
    public static HistoryScopeClause Select(HistoryScope? scope, out bool nothingVisible)
    {
        nothingVisible = false;
        if (scope is null || scope is { VisibleLocations: null, VisibleDevices: null })
        {
            return HistoryScopeClause.Unrestricted;
        }

        var useLocations = scope.VisibleDevices is null || (scope.VisibleLocations is not null && scope.VisibleLocations.Count <= scope.VisibleDevices.Count);
        var chosen = useLocations ? scope.VisibleLocations!.Select(l => l.Value).ToList() : scope.VisibleDevices!.Select(d => d.Value).ToList();
        nothingVisible = chosen.Count == 0;
        return useLocations ? new HistoryScopeClause(chosen, null) : new HistoryScopeClause(null, chosen);
    }
}
