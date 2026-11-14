using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal.Log;

/// <summary>Creates durable consumers (spec PIP §5.4).</summary>
internal sealed partial class LogConsumerFactory(
    ILogBroker broker, PipelineOptions options, TimeProvider timeProvider, ILoggerFactory loggerFactory) : ILogConsumerFactory
{
    private const int MaxBatchLimit = 10_000;

    private readonly LogNaming _naming = new(options);

    public ILogConsumer CreateDurable(string name, LogStream stream, IReadOnlyList<string> kindFilter, LogConsumerOptions options1)
    {
        ArgumentNullException.ThrowIfNull(kindFilter);
        ArgumentNullException.ThrowIfNull(options1);
        if (name is null || !NamePattern().IsMatch(name))
        {
            throw new ArgumentException("A consumer name is 1 to 64 letters, digits, '_' or '-'.", nameof(name));
        }

        foreach (var pattern in kindFilter)
        {
            if (!LogNaming.IsValidKindPattern(pattern))
            {
                throw new ArgumentException($"'{pattern}' is not a record kind or kind pattern.", nameof(kindFilter));
            }
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(options1.MaxBatch, 1, nameof(options1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options1.MaxBatch, MaxBatchLimit, nameof(options1));
        ArgumentOutOfRangeException.ThrowIfLessThan(options1.MaxDeliver, 1, nameof(options1));
        if (options1.MaxWait is { } maxWait && maxWait <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options1), maxWait, "MaxWait must be positive.");
        }

        var ackWait = options1.AckWait ?? TimeSpan.FromSeconds(60);
        if (ackWait < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options1), ackWait, "AckWait must be at least 1 s.");
        }

        var settings = new BrokerConsumerSettings(name, _naming.KindFilters(stream, kindFilter), ackWait, Math.Max(options1.MaxBatch, 1000));
        return new LogConsumer(
            broker, _naming.StreamName(stream), settings, options1, options, timeProvider, loggerFactory.CreateLogger<LogConsumer>());
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
