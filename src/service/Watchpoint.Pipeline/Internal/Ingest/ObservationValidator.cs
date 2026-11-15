using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal.Ingest;

/// <summary>The result of validating one observation: exactly one of an item, an error, or "disabled".</summary>
internal readonly record struct ValidationOutcome(IngestItem? Item, Error? Error)
{
    public bool Disabled => Item is null && Error is null;
}

/// <summary>
/// Validates and normalises observations (spec PIP §7.1): the seven steps in order, the first failure rejects. Never
/// throws for bad input and holds no state, so it is called from any thread.
/// </summary>
internal sealed class ObservationValidator(IIngestDirectory directory, IValueValidator values, IngestOptions options)
{
    private static readonly PropertyPath _faultsPath = PropertyPath.FromTrusted("health.faults");
    private static readonly IReadOnlyDictionary<string, WpValue> _noPayload = new Dictionary<string, WpValue>();

    /// <summary>Step 1 only: the shape of the observation, checked before it may wait for the quota.</summary>
    public static Error? CheckShape(Observation observation)
    {
        if (observation.Id == Guid.Empty)
        {
            return PipelineErrors.ObservationMalformed("The observation has no id.");
        }

        if (observation.Device is null == observation.Server is null)
        {
            return PipelineErrors.ObservationMalformed("Exactly one of Device and Server must be set.");
        }

        var hasProperty = observation.Property is not null;
        var hasEvent = observation.Event is not null;
        var hasHealth = observation.Health is not null;
        var ok = observation.Kind switch
        {
            ObservationKind.Property => hasProperty && !hasEvent && !hasHealth && observation.Payload is null,
            ObservationKind.Event => hasEvent && !hasProperty && !hasHealth,
            ObservationKind.Health => hasHealth && !hasProperty && !hasEvent && observation.Payload is null
                && observation.Health!.Faults is not null && Enum.IsDefined(observation.Health.Connectivity),
            _ => false,
        };
        if (!ok)
        {
            return PipelineErrors.ObservationMalformed($"The fields do not match the kind {observation.Kind}.");
        }

        return Enum.IsDefined(observation.Quality) ? null : PipelineErrors.ObservationMalformed("Unknown quality.");
    }

    public ValidationOutcome Validate(Observation observation, DateTimeOffset receivedTime, IngestCall call)
    {
        // Step 1: shape.
        if (CheckShape(observation) is { } malformed)
        {
            return new ValidationOutcome(null, malformed);
        }

        // Step 2: the target exists and belongs to the reporting connection.
        var targetId = observation.Device?.Value ?? observation.Server!.Value.Value;
        var target = observation.Device is { } device ? directory.FindDevice(device) : directory.FindServer(observation.Server!.Value);
        if (target is null)
        {
            return new ValidationOutcome(null, PipelineErrors.TargetUnknown(targetId));
        }

        if (target.Connection != observation.Connection)
        {
            return new ValidationOutcome(null, PipelineErrors.TargetForeign(targetId));
        }

        // Step 3: disabled targets are dropped silently.
        if (!target.Enabled)
        {
            return default;
        }

        // Steps 4, 5 and 7: the member exists in the type, the value is valid; MOD normalises it.
        var value = WpValue.Null;
        var severity = Severity.Normal;
        IReadOnlyDictionary<string, WpValue>? payload = null;
        HealthRecordBody? health = null;
        switch (observation.Kind)
        {
            case ObservationKind.Property:
                var propertyPath = observation.Property!.Value;
                if (target.Type is null
                    || !target.Type.Properties.TryGetValue(propertyPath, out var property)
                    || target.UnsupportedCapabilities.Contains(propertyPath.Capability))
                {
                    return new ValidationOutcome(null, Error.NotFound(ModelErrors.PropertyNotFound.Value, ("property", propertyPath.Value)));
                }

                var normalised = values.ValidateProperty(property, observation.Value, observation.ReportedUnit);
                if (!normalised.IsSuccess)
                {
                    return new ValidationOutcome(null, normalised.Error);
                }

                value = normalised.Value;
                break;

            case ObservationKind.Event:
                var eventPath = observation.Event!.Value;
                if (target.Type is null
                    || !target.Type.Events.TryGetValue(eventPath, out var definition)
                    || target.UnsupportedCapabilities.Contains(eventPath.Capability))
                {
                    return new ValidationOutcome(null, Error.NotFound(ModelErrors.EventNotFound.Value, ("event", eventPath.Value)));
                }

                var validated = values.ValidatePayload(definition, observation.Payload ?? _noPayload);
                if (!validated.IsSuccess)
                {
                    return new ValidationOutcome(null, validated.Error);
                }

                payload = validated.Value;
                severity = definition.Severity;
                break;

            default:
                var report = observation.Health!;
                var faults = NormaliseFaults(report.Faults, target.Type, out var unknownFault);
                if (faults is null)
                {
                    return new ValidationOutcome(null, unknownFault);
                }

                health = new HealthRecordBody(report.Connectivity, faults, string.IsNullOrEmpty(report.Reason) ? null : report.Reason);
                break;
        }

        // Step 6: a source time too far in the future is replaced by the received time.
        var sourceTime = observation.SourceTime?.ToUniversalTime();
        DateTimeOffset? drifted = null;
        if (sourceTime is { } source && source - receivedTime > options.MaxFutureSkew)
        {
            drifted = source;
            sourceTime = receivedTime;
        }

        return new ValidationOutcome(
            new IngestItem
            {
                Id = observation.Id,
                Kind = observation.Kind,
                Device = observation.Device,
                Server = observation.Server,
                Target = targetId,
                Partition = Partitioning.Of(targetId),
                Property = observation.Property ?? default,
                Value = value,
                Quality = observation.Quality,
                Event = observation.Event ?? default,
                Payload = payload,
                Severity = severity,
                Health = health,
                ReceivedTime = receivedTime,
                SourceTime = sourceTime,
                DriftedSourceTime = drifted,
                Connection = observation.Connection,
                Command = observation.CorrelatedCommand,
                Call = call,
            },
            null);
    }

    /// <summary>
    /// Step 7: every fault is a code of the type's <c>health.faults</c> enum. The faults are a set: they are returned
    /// without duplicates in ordinal order, so that the same state always compares equal.
    /// </summary>
    private static IReadOnlyList<string>? NormaliseFaults(IReadOnlyList<string> faults, DeviceTypeDefinition? type, out Error? error)
    {
        error = null;
        if (faults.Count == 0)
        {
            return [];
        }

        PropertyDefinition? definition = null;
        type?.Properties.TryGetValue(_faultsPath, out definition);
        var sorted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var fault in faults)
        {
            var known = false;
            if (definition is not null && fault is not null)
            {
                foreach (var allowed in definition.EnumValues)
                {
                    if (string.Equals(allowed.Code, fault, StringComparison.Ordinal))
                    {
                        known = true;
                        break;
                    }
                }
            }

            if (!known)
            {
                error = ModelErrors.EnumUnknownError(
                    fault ?? string.Empty,
                    definition?.EnumValues.Select(e => e.Code).ToList() ?? [],
                    _faultsPath.Value);
                return null;
            }

            sorted.Add(fault!);
        }

        return [.. sorted];
    }
}
