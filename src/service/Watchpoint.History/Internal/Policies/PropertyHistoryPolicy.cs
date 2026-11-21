using Watchpoint.Storage.Contracts;

namespace Watchpoint.History.Internal.Policies;

/// <summary>The recording modes of a property (spec HIS §6).</summary>
internal enum HistoryMode
{
    EveryChange,
    Deadband,
    Periodic,
    None,
}

/// <summary>A property history policy: mode and parameters.</summary>
/// <param name="Mode">The mode.</param>
/// <param name="Percent">Whether the deadband threshold is a percentage of the last stored value (else absolute).</param>
/// <param name="Threshold">The deadband threshold (a percentage such as 5 for 5 %).</param>
/// <param name="Interval">The sampling interval of <see cref="HistoryMode.Periodic"/>.</param>
internal sealed record PropertyHistoryPolicy(HistoryMode Mode, bool Percent = false, decimal Threshold = 0, TimeSpan? Interval = null)
{
    /// <summary>A stored value is repeated at least this often under a deadband policy.</summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromHours(1);

    public static PropertyHistoryPolicy EveryChange { get; } = new(HistoryMode.EveryChange);

    /// <summary>Reads the policy of a type definition (<c>all</c> is the catalogue's name of <c>everyChange</c>) or an override; unknown modes record every change.</summary>
    public static PropertyHistoryPolicy From(string? mode, string? type, double? threshold, TimeSpan? interval) => mode switch
    {
        "deadband" => new(HistoryMode.Deadband, string.Equals(type, "percent", StringComparison.Ordinal), (decimal)(threshold ?? 0), null),
        "periodic" => new(HistoryMode.Periodic, Interval: interval ?? TimeSpan.FromMinutes(1)),
        "none" => new(HistoryMode.None),
        _ => EveryChange,
    };

    /// <summary>
    /// Decides whether a property record is stored. Deadband stores the first value, a quality change, a change of a non-numeric
    /// value, a numeric change of at least the threshold (absolute, or the percentage of the last stored value), and a value after
    /// <see cref="MaxInterval"/> without a stored one.
    /// </summary>
    /// <param name="candidate">The row that would be stored.</param>
    /// <param name="lastStored">The last stored row of the property, or <see langword="null"/> when none.</param>
    /// <returns><see langword="true"/> to store.</returns>
    public bool ShouldStore(HistoryPropertyRow candidate, HistoryPropertyRow? lastStored)
    {
        switch (Mode)
        {
            case HistoryMode.EveryChange:
                return true;
            case HistoryMode.None:
            case HistoryMode.Periodic:
                return false;
        }

        if (lastStored is null || candidate.Quality != lastStored.Quality || candidate.Time - lastStored.Time >= MaxInterval)
        {
            return true;
        }

        if (candidate.VNum is { } now && lastStored.VNum is { } before)
        {
            var delta = Math.Abs(now - before);
            var limit = Percent ? Threshold / 100m * Math.Abs(before) : Threshold;
            return delta > 0 && delta >= limit;
        }

        return candidate.VNum != lastStored.VNum || candidate.VText != lastStored.VText || candidate.VBool != lastStored.VBool || candidate.VJson != lastStored.VJson;
    }
}
