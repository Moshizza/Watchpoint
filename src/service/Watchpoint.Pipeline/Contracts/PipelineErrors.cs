using Watchpoint.Kernel.Results;

namespace Watchpoint.Pipeline.Contracts;

/// <summary>Errors of the pipeline (spec PIP §11).</summary>
public static class PipelineErrors
{
    /// <summary>Code of <see cref="BatchTooLarge"/>.</summary>
    public const string BatchTooLargeCode = "pipeline.batch.too_large";

    /// <summary>Code of <see cref="RecordInvalid"/>.</summary>
    public const string RecordInvalidCode = "pipeline.record.invalid";

    /// <summary>Code of <see cref="RecordTooLarge"/>.</summary>
    public const string RecordTooLargeCode = "pipeline.record.too_large";

    /// <summary>Code of <see cref="LogUnavailable"/>.</summary>
    public const string LogUnavailableCode = "pipeline.log.unavailable";

    /// <summary>Code of <see cref="StreamIncompatible"/>.</summary>
    public const string StreamIncompatibleCode = "pipeline.stream.incompatible";

    /// <summary>Code of <see cref="ObservationMalformed"/>.</summary>
    public const string ObservationMalformedCode = "pipeline.observation.malformed";

    /// <summary>Code of <see cref="TargetUnknown"/>.</summary>
    public const string TargetUnknownCode = "pipeline.target.unknown";

    /// <summary>Code of <see cref="TargetForeign"/>.</summary>
    public const string TargetForeignCode = "pipeline.target.foreign";

    /// <summary>An observation misses a field its kind needs, or holds contradictory fields.</summary>
    /// <param name="reason">What is wrong (for developers).</param>
    /// <returns>The error (Validation).</returns>
    public static Error ObservationMalformed(string reason) =>
        Error.Create(ErrorKind.Validation, ObservationMalformedCode) with { DeveloperDetail = reason };

    /// <summary>The device or server of an observation is not in the registry.</summary>
    /// <param name="target">The device or server id.</param>
    /// <returns>The error (NotFound).</returns>
    public static Error TargetUnknown(Guid target) =>
        Error.Create(ErrorKind.NotFound, TargetUnknownCode, ("target", target.ToString()));

    /// <summary>The device or server of an observation belongs to another connection.</summary>
    /// <param name="target">The device or server id.</param>
    /// <returns>The error (Forbidden).</returns>
    public static Error TargetForeign(Guid target) =>
        Error.Create(ErrorKind.Forbidden, TargetForeignCode, ("target", target.ToString()));

    /// <summary>An ingest batch holds more observations than allowed.</summary>
    /// <param name="count">The number of observations.</param>
    /// <param name="max">The limit.</param>
    /// <returns>The error (Validation).</returns>
    public static Error BatchTooLarge(int count, int max) =>
        Error.Create(ErrorKind.Validation, BatchTooLargeCode, ("count", count), ("max", max));

    /// <summary>A record cannot be appended: its envelope is incomplete or contradictory, or its kind is not registered.</summary>
    /// <param name="recordId">The record id.</param>
    /// <param name="reason">What is wrong (for developers; never contains the body).</param>
    /// <returns>The error (Validation).</returns>
    public static Error RecordInvalid(Guid recordId, string reason) =>
        Error.Create(ErrorKind.Validation, RecordInvalidCode, ("recordId", recordId.ToString())) with { DeveloperDetail = reason };

    /// <summary>A record is larger than the maximum message size of the log.</summary>
    /// <param name="recordId">The record id.</param>
    /// <param name="size">The size of the message (headers and body) in bytes.</param>
    /// <param name="max">The limit in bytes.</param>
    /// <returns>The error (Validation).</returns>
    public static Error RecordTooLarge(Guid recordId, long size, long max) =>
        Error.Create(ErrorKind.Validation, RecordTooLargeCode, ("recordId", recordId.ToString()), ("size", size), ("max", max));

    /// <summary>JetStream is not reachable or did not acknowledge in time.</summary>
    /// <param name="reason">The last failure (for developers).</param>
    /// <returns>The error (Unavailable).</returns>
    public static Error LogUnavailable(string reason) =>
        Error.Create(ErrorKind.Unavailable, LogUnavailableCode) with { DeveloperDetail = reason };

    /// <summary>An existing stream conflicts with the configuration and cannot be updated.</summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="reason">The conflicting settings (for developers).</param>
    /// <returns>The error (Internal).</returns>
    public static Error StreamIncompatible(string stream, string reason) =>
        Error.Create(ErrorKind.Internal, StreamIncompatibleCode, ("stream", stream)) with { DeveloperDetail = reason };
}
