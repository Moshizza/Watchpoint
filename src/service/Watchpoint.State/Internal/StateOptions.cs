using System.Globalization;
using System.Xml;
using Microsoft.Extensions.Configuration;

namespace Watchpoint.State.Internal;

/// <summary>Settings of the state module (spec STA §9, section <c>State</c>).</summary>
internal sealed class StateOptions
{
    public const string SectionName = "State";

    /// <summary><c>State:SnapshotInterval</c>: reserved for the snapshotter (T1.12); PT1S–PT5M.</summary>
    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary><c>State:RecentChanges</c>: the capacity of the resync ring; 10,000–5,000,000.</summary>
    public int RecentChanges { get; set; } = 200_000;

    /// <summary><c>State:ListenerDefaultCapacity</c>: the default bounded queue capacity of a change feed listener.</summary>
    public int ListenerDefaultCapacity { get; set; } = 10_000;

    /// <summary>How often the aggregate consistency check runs (spec STA §6.4); not a configuration setting.</summary>
    public TimeSpan ConsistencyCheckInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Reads and validates the <c>State</c> section.</summary>
    /// <exception cref="InvalidOperationException">A setting is out of range.</exception>
    public static StateOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var options = new StateOptions();
        var problems = new List<string>();
        options.SnapshotInterval = ReadDuration(section, nameof(SnapshotInterval), options.SnapshotInterval, problems);
        options.RecentChanges = ReadInt(section, nameof(RecentChanges), options.RecentChanges, problems);
        options.ListenerDefaultCapacity = ReadInt(section, nameof(ListenerDefaultCapacity), options.ListenerDefaultCapacity, problems);
        problems.AddRange(options.Validate());
        return problems.Count == 0
            ? options
            : throw new InvalidOperationException("Invalid state settings: " + string.Join(" ", problems));
    }

    /// <summary>Checks the configurable settings against their ranges (spec STA §9).</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (SnapshotInterval < TimeSpan.FromSeconds(1) || SnapshotInterval > TimeSpan.FromMinutes(5))
        {
            problems.Add("State:SnapshotInterval must be between PT1S and PT5M.");
        }

        if (RecentChanges is < 10_000 or > 5_000_000)
        {
            problems.Add("State:RecentChanges must be between 10,000 and 5,000,000.");
        }

        if (ListenerDefaultCapacity < 1)
        {
            problems.Add("State:ListenerDefaultCapacity must be at least 1.");
        }

        return problems;
    }

    private static int ReadInt(IConfiguration section, string key, int fallback, List<string> problems)
    {
        if (section[key] is not { } text)
        {
            return fallback;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        problems.Add($"State:{key} '{text}' is not a number.");
        return fallback;
    }

    private static TimeSpan ReadDuration(IConfiguration section, string key, TimeSpan fallback, List<string> problems)
    {
        if (section[key] is not { } text)
        {
            return fallback;
        }

        try
        {
            return XmlConvert.ToTimeSpan(text);
        }
        catch (FormatException)
        {
            problems.Add($"State:{key} '{text}' is not an ISO 8601 duration.");
            return fallback;
        }
    }
}
