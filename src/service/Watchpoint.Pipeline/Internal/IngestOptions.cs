using System.Globalization;
using System.Xml;
using Microsoft.Extensions.Configuration;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal;

/// <summary>
/// Settings of the ingest and of the side-effect queue (spec PIP §10, sections <c>Ingest</c> and <c>SideEffects</c>).
/// The members after <see cref="TickInterval"/> are fixed by the spec and not read from the configuration; tests
/// shorten them.
/// </summary>
internal sealed class IngestOptions
{
    public const string SectionName = "Ingest";
    public const string SideEffectsSectionName = "SideEffects";

    /// <summary><c>Ingest:PartitionQueueCapacity</c>: observations waiting per partition (100–1,000,000).</summary>
    public int PartitionQueueCapacity { get; set; } = 10_000;

    /// <summary><c>Ingest:MaxBatch</c>: observations processed and appended together (1–5,000).</summary>
    public int MaxBatch { get; set; } = 500;

    /// <summary><c>Ingest:Linger</c>: how long a worker waits for more input after the first observation (0–PT0.1S).</summary>
    public TimeSpan Linger { get; set; } = TimeSpan.FromMilliseconds(2);

    /// <summary><c>Ingest:ConnectionRate</c>: observations per second of one connection (≥ 1).</summary>
    public int ConnectionRate { get; set; } = 2000;

    /// <summary><c>Ingest:ConnectionBurst</c>: size of the token bucket of one connection (≥ 1).</summary>
    public int ConnectionBurst { get; set; } = 20_000;

    /// <summary><c>Ingest:MaxFutureSkew</c>: how far in the future a source time may be (≥ 0).</summary>
    public TimeSpan MaxFutureSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary><c>Ingest:TickInterval</c>: cadence of <c>OnTick</c> (PT0.1S–PT10S).</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary><c>Ingest:SubscriberBudget</c>: a subscriber call longer than this is logged (1646), never interrupted (PT0.001S–PT1S).</summary>
    public TimeSpan SubscriberBudget { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>Levels of follow-up records appended for one input batch.</summary>
    public int MaxFollowUpDepth { get; set; } = 4;

    /// <summary>Posted work items waiting per partition.</summary>
    public int ControlQueueCapacity { get; set; } = 1024;

    /// <summary>How long coalescing may last before the connection is flooding.</summary>
    public TimeSpan FloodingAfter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Waiting events and health observations above which the connection is flooding.</summary>
    public int FloodingHigh { get; set; } = 100_000;

    /// <summary>Waiting events and health observations below which a flooding connection may ingest again.</summary>
    public int FloodingLow { get; set; } = 50_000;

    /// <summary>How often rejections are logged per connection and error code (log 1620).</summary>
    public TimeSpan RejectionLogInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long the workers may finish queued work at shutdown.</summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The first delay between two runs of a failing side effect; doubled for each further run.</summary>
    public TimeSpan SideEffectRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest delay between two runs of a failing side effect.</summary>
    public TimeSpan SideEffectRetryDelayMax { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Ids of side effects remembered per class to ignore duplicates.</summary>
    public int SideEffectIdMemory { get; set; } = 65_536;

    /// <summary><c>SideEffects:Capacity:&lt;Class&gt;</c>: effects waiting per class (≥ 1).</summary>
    public Dictionary<SideEffectClass, int> SideEffectCapacity { get; } = Enum.GetValues<SideEffectClass>().ToDictionary(c => c, _ => 10_000);

    /// <summary><c>SideEffects:Workers:&lt;Class&gt;</c>: concurrent runs per class (1–64).</summary>
    public Dictionary<SideEffectClass, int> SideEffectWorkers { get; } =
        Enum.GetValues<SideEffectClass>().ToDictionary(c => c, c => c == SideEffectClass.Background ? 2 : 4);

    /// <summary>Reads and validates the <c>Ingest</c> and <c>SideEffects</c> sections.</summary>
    /// <exception cref="InvalidOperationException">A setting is out of range.</exception>
    public static IngestOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var options = new IngestOptions();
        var problems = new List<string>();
        options.PartitionQueueCapacity = ReadInt(section, "Ingest", "PartitionQueueCapacity", options.PartitionQueueCapacity, problems);
        options.MaxBatch = ReadInt(section, "Ingest", "MaxBatch", options.MaxBatch, problems);
        options.ConnectionRate = ReadInt(section, "Ingest", "ConnectionRate", options.ConnectionRate, problems);
        options.ConnectionBurst = ReadInt(section, "Ingest", "ConnectionBurst", options.ConnectionBurst, problems);
        options.Linger = ReadDuration(section, "Linger", options.Linger, problems);
        options.MaxFutureSkew = ReadDuration(section, "MaxFutureSkew", options.MaxFutureSkew, problems);
        options.TickInterval = ReadDuration(section, "TickInterval", options.TickInterval, problems);
        options.SubscriberBudget = ReadDuration(section, "SubscriberBudget", options.SubscriberBudget, problems);

        var sideEffects = configuration.GetSection(SideEffectsSectionName);
        foreach (var effectClass in Enum.GetValues<SideEffectClass>())
        {
            var name = effectClass.ToString();
            options.SideEffectCapacity[effectClass] =
                ReadInt(sideEffects.GetSection("Capacity"), "SideEffects:Capacity", name, options.SideEffectCapacity[effectClass], problems);
            options.SideEffectWorkers[effectClass] =
                ReadInt(sideEffects.GetSection("Workers"), "SideEffects:Workers", name, options.SideEffectWorkers[effectClass], problems);
        }

        problems.AddRange(options.Validate());
        return problems.Count == 0
            ? options
            : throw new InvalidOperationException("Invalid ingest settings: " + string.Join(" ", problems));
    }

    /// <summary>Checks the configurable settings against their ranges (spec PIP §10).</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (PartitionQueueCapacity is < 100 or > 1_000_000)
        {
            problems.Add("Ingest:PartitionQueueCapacity must be between 100 and 1,000,000.");
        }

        if (MaxBatch is < 1 or > IngestLimits.MaxBatchSize)
        {
            problems.Add("Ingest:MaxBatch must be between 1 and 5,000.");
        }

        if (Linger < TimeSpan.Zero || Linger > TimeSpan.FromMilliseconds(100))
        {
            problems.Add("Ingest:Linger must be between 0 and PT0.1S.");
        }

        if (ConnectionRate < 1 || ConnectionBurst < 1)
        {
            problems.Add("Ingest:ConnectionRate and Ingest:ConnectionBurst must be at least 1.");
        }

        if (MaxFutureSkew < TimeSpan.Zero)
        {
            problems.Add("Ingest:MaxFutureSkew must not be negative.");
        }

        if (TickInterval < TimeSpan.FromMilliseconds(100) || TickInterval > TimeSpan.FromSeconds(10))
        {
            problems.Add("Ingest:TickInterval must be between PT0.1S and PT10S.");
        }

        if (SubscriberBudget < TimeSpan.FromMilliseconds(1) || SubscriberBudget > TimeSpan.FromSeconds(1))
        {
            problems.Add("Ingest:SubscriberBudget must be between PT0.001S and PT1S.");
        }

        foreach (var effectClass in Enum.GetValues<SideEffectClass>())
        {
            if (SideEffectCapacity[effectClass] < 1)
            {
                problems.Add($"SideEffects:Capacity:{effectClass} must be at least 1.");
            }

            if (SideEffectWorkers[effectClass] is < 1 or > 64)
            {
                problems.Add($"SideEffects:Workers:{effectClass} must be between 1 and 64.");
            }
        }

        return problems;
    }

    private static int ReadInt(IConfiguration section, string prefix, string key, int fallback, List<string> problems)
    {
        if (section[key] is not { } text)
        {
            return fallback;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        problems.Add($"{prefix}:{key} '{text}' is not a number.");
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
            problems.Add($"Ingest:{key} '{text}' is not an ISO 8601 duration.");
            return fallback;
        }
    }
}
