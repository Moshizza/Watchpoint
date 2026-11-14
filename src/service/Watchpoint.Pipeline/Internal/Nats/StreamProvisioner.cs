using Microsoft.Extensions.Logging;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal.Log;

namespace Watchpoint.Pipeline.Internal.Nats;

/// <summary>
/// Creates or updates the streams of the event log at start-up (spec PIP §5.1; run by the host in phase
/// <c>Infrastructure</c>, order 20), idempotently:
/// <list type="bullet">
/// <item>a missing stream is created;</item>
/// <item>a stream whose settings drifted (age, size, duplicate window, message size, replicas, direct access, discard
/// policy) is updated;</item>
/// <item>a stream that differs in a setting JetStream cannot change (subjects are never changed by this node; storage
/// type, retention policy, delete and purge protection) stops the start-up with <c>pipeline.stream.incompatible</c>;</item>
/// <item>JetStream reserves the maximum size of a stream: when it has less storage, the size is halved until it fits
/// (never below the current size of an existing stream) and a warning is logged.</item>
/// </list>
/// </summary>
internal sealed class StreamProvisioner(
    ILogBroker broker, PipelineOptions options, TimeProvider timeProvider, ILogger<StreamProvisioner> logger) : IEventLogSetup
{
    private readonly LogNaming _naming = new(options);
    private volatile bool _ready;

    /// <summary>Gets a value indicating whether the streams were found or created since start-up.</summary>
    public bool Ready => _ready;

    /// <summary>
    /// The stream settings the configuration asks for. The small global streams come first: when JetStream is short
    /// of storage it is the size of <c>WP_LOG</c> that gives way.
    /// </summary>
    public IReadOnlyList<StreamSettings> DesiredStreams() =>
    [
        Desired(LogStream.Cfg, options.GlobalRetention, options.GlobalMaxBytes),
        Desired(LogStream.Sys, options.GlobalRetention, options.GlobalMaxBytes),
        Desired(LogStream.Log, options.Retention, options.MaxBytes),
    ];

    public async Task EnsureStreamsAsync(CancellationToken ct)
    {
        var desired = DesiredStreams();
        foreach (var stream in desired)
        {
            await BrokerRetry.RunAsync(
                async token =>
                {
                    await EnsureAsync(stream, token).ConfigureAwait(false);
                    return true;
                },
                options,
                timeProvider,
                logger,
                ct).ConfigureAwait(false);
        }

        _ready = true;
        var names = string.Join(", ", desired.Select(s => s.Name));
        PipelineLog.StreamsReady(logger, names);
    }

    private static List<string> ImmutableDifferences(StreamSettings desired, StreamSettings existing)
    {
        var differences = new List<string>();
        if (!desired.Subjects.ToHashSet(StringComparer.Ordinal).SetEquals(existing.Subjects))
        {
            differences.Add($"subjects are [{string.Join(", ", existing.Subjects)}], expected [{string.Join(", ", desired.Subjects)}]");
        }

        if (!existing.FileStorage)
        {
            differences.Add("storage is not file");
        }

        if (!existing.LimitsRetention)
        {
            differences.Add("retention policy is not limits");
        }

        if (!existing.DenyDelete)
        {
            differences.Add("message deletion is allowed");
        }

        if (!existing.DenyPurge)
        {
            differences.Add("purge is allowed");
        }

        return differences;
    }

    private static List<string> Drift(StreamSettings desired, StreamSettings existing)
    {
        var changes = new List<string>();
        if (desired.MaxAge != existing.MaxAge)
        {
            changes.Add($"max age {existing.MaxAge} -> {desired.MaxAge}");
        }

        if (desired.MaxBytes != existing.MaxBytes)
        {
            changes.Add($"max bytes {existing.MaxBytes} -> {desired.MaxBytes}");
        }

        if (desired.DuplicateWindow != existing.DuplicateWindow)
        {
            changes.Add($"duplicate window {existing.DuplicateWindow} -> {desired.DuplicateWindow}");
        }

        if (desired.MaxMessageSize != existing.MaxMessageSize)
        {
            changes.Add($"max message size {existing.MaxMessageSize} -> {desired.MaxMessageSize}");
        }

        if (desired.Replicas != existing.Replicas)
        {
            changes.Add($"replicas {existing.Replicas} -> {desired.Replicas}");
        }

        if (!existing.AllowDirect)
        {
            changes.Add("direct access enabled");
        }

        if (!existing.DiscardOld)
        {
            changes.Add("discard policy -> old");
        }

        return changes;
    }

    private StreamSettings Desired(LogStream stream, TimeSpan maxAge, long maxBytes) => new()
    {
        Name = _naming.StreamName(stream),
        Subjects = [_naming.StreamSubjects(stream)],
        MaxAge = maxAge,
        MaxBytes = maxBytes,
        DuplicateWindow = options.DuplicateWindow,
        MaxMessageSize = options.MaxMessageSize,
        Replicas = options.Replicas,
    };

    private async ValueTask EnsureAsync(StreamSettings desired, CancellationToken ct)
    {
        var existing = await broker.GetStreamAsync(desired.Name, ct).ConfigureAwait(false);
        if (existing is null)
        {
            await ApplyFittingAsync(desired, null, ct).ConfigureAwait(false);
            return;
        }

        var incompatible = ImmutableDifferences(desired, existing);
        if (incompatible.Count > 0)
        {
            throw new PipelineException(PipelineErrors.StreamIncompatible(desired.Name, string.Join("; ", incompatible)));
        }

        if (Drift(desired, existing).Count == 0)
        {
            return;
        }

        var applied = await ApplyFittingAsync(desired, existing, ct).ConfigureAwait(false);
        var changes = Drift(applied, existing);
        if (changes.Count > 0)
        {
            var summary = string.Join("; ", changes);
            PipelineLog.StreamUpdated(logger, desired.Name, summary);
        }
    }

    /// <summary>Creates (<paramref name="existing"/> null) or updates the stream with the largest size JetStream can reserve.</summary>
    private async ValueTask<StreamSettings> ApplyFittingAsync(StreamSettings desired, StreamSettings? existing, CancellationToken ct)
    {
        var candidate = desired;
        while (true)
        {
            try
            {
                if (existing is null)
                {
                    await broker.CreateStreamAsync(candidate, ct).ConfigureAwait(false);
                }
                else if (Drift(candidate, existing).Count > 0)
                {
                    await broker.UpdateStreamAsync(candidate, ct).ConfigureAwait(false);
                }

                if (candidate.MaxBytes != desired.MaxBytes)
                {
                    PipelineLog.StreamReduced(logger, desired.Name, candidate.MaxBytes, desired.MaxBytes);
                }

                return candidate;
            }
            catch (BrokerStorageException ex)
            {
                var floor = Math.Max(options.MinStreamBytes, existing?.MaxBytes ?? 0);
                if (candidate.MaxBytes <= floor)
                {
                    throw new PipelineException(
                        PipelineErrors.StreamIncompatible(desired.Name, $"JetStream cannot reserve {candidate.MaxBytes} bytes: {ex.Message}"), ex);
                }

                candidate = candidate with { MaxBytes = Math.Max(candidate.MaxBytes / 2, floor) };
            }
            catch (BrokerRejectedException ex)
            {
                throw new PipelineException(PipelineErrors.StreamIncompatible(desired.Name, ex.Message), ex);
            }
        }
    }
}
