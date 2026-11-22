using System.Globalization;
using System.Xml;
using Microsoft.Extensions.Configuration;

namespace Watchpoint.Commands.Internal;

/// <summary>Settings of the commands module (section <c>Commands</c>, spec CMD §12) and the fixed limits of CMD §5–§6.</summary>
internal sealed class CommandsOptions
{
    public const string SectionName = "Commands";

    /// <summary>Longest reason, in characters (CMD §5 step 7).</summary>
    public const int MaxReasonLength = 500;

    /// <summary>Longest idempotency key (CMD §4).</summary>
    public const int MaxClientCommandIdLength = 64;

    /// <summary>Largest group (CMD §10).</summary>
    public const int MaxGroupSize = 5_000;

    /// <summary>How far ahead <c>NotBefore</c> may be (CMD §5 step 10).</summary>
    public static readonly TimeSpan MaxNotBeforeAhead = TimeSpan.FromHours(24);

    /// <summary>A finished command stays in the pending store this long (CMD §6).</summary>
    public static readonly TimeSpan TerminalRetention = TimeSpan.FromSeconds(60);

    /// <summary>A finished command stays readable through <c>Get</c> this long (CMD §5).</summary>
    public static readonly TimeSpan ReadCacheRetention = TimeSpan.FromHours(24);

    public TimeSpan DefaultExpiry { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ApprovalWindow { get; set; } = TimeSpan.FromMinutes(5);

    public bool FailFastWhenDisconnected { get; set; } = true;

    public int MaxInFlightPerConnection { get; set; } = 8;

    public int BatchSize { get; set; } = 100;

    public bool StepUpForCritical { get; set; } = true;

    public TimeSpan IdempotencyWindow { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Reads the <c>Commands</c> section.</summary>
    /// <exception cref="InvalidOperationException">A setting is invalid.</exception>
    public static CommandsOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var options = new CommandsOptions();
        var problems = new List<string>();
        options.DefaultExpiry = Duration(section, nameof(DefaultExpiry), options.DefaultExpiry, problems);
        options.ApprovalWindow = Duration(section, nameof(ApprovalWindow), options.ApprovalWindow, problems);
        options.IdempotencyWindow = Duration(section, nameof(IdempotencyWindow), options.IdempotencyWindow, problems);
        options.FailFastWhenDisconnected = Flag(section, nameof(FailFastWhenDisconnected), options.FailFastWhenDisconnected, problems);
        options.StepUpForCritical = Flag(section, nameof(StepUpForCritical), options.StepUpForCritical, problems);
        options.MaxInFlightPerConnection = Number(section, nameof(MaxInFlightPerConnection), options.MaxInFlightPerConnection, 1, 256, problems);
        options.BatchSize = Number(section, nameof(BatchSize), options.BatchSize, 1, 1_000, problems);
        if (options.DefaultExpiry <= TimeSpan.Zero || options.ApprovalWindow <= TimeSpan.Zero || options.IdempotencyWindow <= TimeSpan.Zero)
        {
            problems.Add("Commands:DefaultExpiry, ApprovalWindow and IdempotencyWindow must be positive.");
        }

        return problems.Count == 0
            ? options
            : throw new InvalidOperationException("Invalid commands settings: " + string.Join(" ", problems));
    }

    private static TimeSpan Duration(IConfigurationSection section, string key, TimeSpan fallback, List<string> problems)
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
            problems.Add($"Commands:{key} '{text}' is not an ISO 8601 duration.");
            return fallback;
        }
    }

    private static bool Flag(IConfigurationSection section, string key, bool fallback, List<string> problems)
    {
        if (section[key] is not { } text)
        {
            return fallback;
        }

        if (bool.TryParse(text, out var value))
        {
            return value;
        }

        problems.Add($"Commands:{key} '{text}' is not true or false.");
        return fallback;
    }

    private static int Number(IConfigurationSection section, string key, int fallback, int min, int max, List<string> problems)
    {
        if (section[key] is not { } text)
        {
            return fallback;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
        {
            return value;
        }

        problems.Add($"Commands:{key} '{text}' is not a number from {min} to {max}.");
        return fallback;
    }
}
