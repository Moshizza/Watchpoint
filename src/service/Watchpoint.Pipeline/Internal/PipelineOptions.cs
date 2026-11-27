using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Extensions.Configuration;

namespace Watchpoint.Pipeline.Internal;

/// <summary>
/// Settings of the event log (spec PIP §10, section <c>Log</c>). The members after <see cref="AppendTimeout"/> are fixed
/// by the spec and not read from the configuration; tests shorten them.
/// </summary>
internal sealed partial class PipelineOptions
{
    public const string SectionName = "Log";

    private const long Gibibyte = 1024L * 1024 * 1024;

    /// <summary><c>Log:Retention</c>: maximum age of <c>WP_LOG</c> records (P1D–P30D: a privacy purge completes within this window, HIS §12).</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary><c>Log:MaxBytes</c>: maximum size of <c>WP_LOG</c> (≥ 1 GB).</summary>
    public long MaxBytes { get; set; } = 20 * Gibibyte;

    /// <summary><c>Log:SubjectPrefix</c>.</summary>
    public string SubjectPrefix { get; set; } = "wp";

    /// <summary><c>Log:StreamPrefix</c>.</summary>
    public string StreamPrefix { get; set; } = string.Empty;

    /// <summary><c>Log:Replicas</c>: 1, 3 or 5.</summary>
    public int Replicas { get; set; } = 1;

    /// <summary>
    /// <c>Log:AppendTimeout</c>: how long an append (or a read or management call) is retried while JetStream is
    /// unreachable before <c>pipeline.log.unavailable</c> (PT1S–PT90S; must stay below <see cref="DuplicateWindow"/>).
    /// </summary>
    public TimeSpan AppendTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <c>Log:AllowInsecureRemote</c>: allows a NATS server that is not on loopback without TLS (spec PIP §13);
    /// for development and tests only.
    /// </summary>
    public bool AllowInsecureRemote { get; set; }

    /// <summary>Maximum age of <c>WP_CFG</c> and <c>WP_SYS</c> records.</summary>
    public TimeSpan GlobalRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Maximum size of <c>WP_CFG</c> and of <c>WP_SYS</c>.</summary>
    public long GlobalMaxBytes { get; set; } = 2 * Gibibyte;

    /// <summary>The JetStream duplicate window (dedup by <c>Nats-Msg-Id</c>).</summary>
    public TimeSpan DuplicateWindow { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The maximum size of a message (headers and body).</summary>
    public int MaxMessageSize { get; set; } = 1024 * 1024;

    /// <summary>How long one publish waits for its acknowledgement before it is repeated.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long one read or management call may take.</summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The first delay between two attempts; doubled up to <see cref="RetryDelayMax"/>.</summary>
    public TimeSpan RetryDelayMin { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The longest delay between two attempts.</summary>
    public TimeSpan RetryDelayMax { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The smallest size a stream is reduced to when JetStream has less storage than configured.</summary>
    public long MinStreamBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Messages asked per request when replaying.</summary>
    public int ReadPageSize { get; set; } = 1000;

    /// <summary>Registry outbox (PIP §5.5): how often the publisher polls for records when it was not signalled.</summary>
    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Registry outbox: the first delay after a failed publish or database call; doubled up to <see cref="OutboxRetryDelayMax"/>.</summary>
    public TimeSpan OutboxRetryDelayMin { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Registry outbox: the longest delay between two attempts.</summary>
    public TimeSpan OutboxRetryDelayMax { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Registry outbox: unsent records read per round.</summary>
    public int OutboxBatchSize { get; set; } = 100;

    /// <summary>Registry outbox: how long a sent record is kept before it is deleted.</summary>
    public TimeSpan OutboxSentRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Registry outbox: how often sent records are pruned (when the publisher is idle).</summary>
    public TimeSpan OutboxPruneInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether a durable consumer is rewound to its last acknowledged record with the JetStream consumer reset API;
    /// otherwise (servers without that API) the consumer is deleted and re-created at that position.
    /// </summary>
    public bool UseConsumerReset { get; set; } = true;

    /// <summary>Reads and validates the <c>Log</c> section.</summary>
    /// <exception cref="InvalidOperationException">A setting is out of range.</exception>
    public static PipelineOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var options = new PipelineOptions();
        var problems = new List<string>();
        if (section["Retention"] is { } retention)
        {
            options.Retention = ParseDuration("Retention", retention, problems);
        }

        if (section["AppendTimeout"] is { } appendTimeout)
        {
            options.AppendTimeout = ParseDuration("AppendTimeout", appendTimeout, problems);
        }

        if (section["MaxBytes"] is { } maxBytes)
        {
            if (TryParseSize(maxBytes, out var bytes))
            {
                options.MaxBytes = bytes;
            }
            else
            {
                problems.Add($"Log:MaxBytes '{maxBytes}' is not a size (bytes, or a number followed by MB, GB or TB).");
            }
        }

        options.SubjectPrefix = section["SubjectPrefix"] ?? options.SubjectPrefix;
        options.StreamPrefix = section["StreamPrefix"] ?? options.StreamPrefix;
        if (section["Replicas"] is { } replicas)
        {
            if (int.TryParse(replicas, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                options.Replicas = count;
            }
            else
            {
                problems.Add($"Log:Replicas '{replicas}' is not a number.");
            }
        }

        if (section["AllowInsecureRemote"] is { } insecure)
        {
            if (bool.TryParse(insecure, out var allow))
            {
                options.AllowInsecureRemote = allow;
            }
            else
            {
                problems.Add($"Log:AllowInsecureRemote '{insecure}' is not true or false.");
            }
        }

        problems.AddRange(options.Validate());
        return problems.Count == 0
            ? options
            : throw new InvalidOperationException("Invalid event log settings: " + string.Join(" ", problems));
    }

    /// <summary>Checks the configurable settings against their ranges (spec PIP §10).</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Retention < TimeSpan.FromDays(1) || Retention > TimeSpan.FromDays(30))
        {
            problems.Add("Log:Retention must be between P1D and P30D.");
        }

        if (MaxBytes < Gibibyte)
        {
            problems.Add("Log:MaxBytes must be at least 1 GB.");
        }

        if (!SubjectPrefixPattern().IsMatch(SubjectPrefix))
        {
            problems.Add("Log:SubjectPrefix must match ^[a-z0-9]{1,16}(\\.[a-z0-9]{1,16})?$.");
        }

        if (!StreamPrefixPattern().IsMatch(StreamPrefix))
        {
            problems.Add("Log:StreamPrefix must match ^[A-Z0-9_]{0,16}$.");
        }

        if (Replicas is not (1 or 3 or 5))
        {
            problems.Add("Log:Replicas must be 1, 3 or 5.");
        }

        if (AppendTimeout < TimeSpan.FromSeconds(1) || AppendTimeout > TimeSpan.FromSeconds(90))
        {
            problems.Add("Log:AppendTimeout must be between PT1S and PT90S.");
        }

        return problems;
    }

    /// <summary>Parses a size: a number of bytes, or a number followed by <c>MB</c>, <c>GB</c> or <c>TB</c> (binary multiples).</summary>
    internal static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        var trimmed = text.Trim();
        long factor = 1;
        foreach (var (suffix, multiplier) in new[] { ("TB", Gibibyte * 1024), ("GB", Gibibyte), ("MB", 1024L * 1024) })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                factor = multiplier;
                trimmed = trimmed[..^suffix.Length].TrimEnd();
                break;
            }
        }

        if (!long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number > long.MaxValue / factor)
        {
            return false;
        }

        bytes = number * factor;
        return true;
    }

    private static TimeSpan ParseDuration(string key, string text, List<string> problems)
    {
        try
        {
            return XmlConvert.ToTimeSpan(text);
        }
        catch (FormatException)
        {
            problems.Add($"Log:{key} '{text}' is not an ISO 8601 duration.");
            return TimeSpan.Zero;
        }
    }

    [GeneratedRegex(@"^[a-z0-9]{1,16}(\.[a-z0-9]{1,16})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex SubjectPrefixPattern();

    [GeneratedRegex(@"^[A-Z0-9_]{0,16}\z", RegexOptions.CultureInvariant)]
    private static partial Regex StreamPrefixPattern();
}
