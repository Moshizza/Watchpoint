using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Time;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Pipeline.Internal;

/// <summary>
/// Publishes applied registry batches as <c>registry.changed</c> records on <c>WP_CFG</c> (spec REG §6, PIP §5.3).
/// The batch is already committed when this is called, so a log failure cannot undo it: it is logged as an error and
/// consumers see a gap in the revisions, which tells them to read the registry again.
/// </summary>
internal sealed class LogRegistryChangePublisher(
    IEventLogWriter writer, ILogRecordTypeRegistry registry, TimeProvider timeProvider, ILogger<LogRegistryChangePublisher> logger)
    : IRegistryChangePublisher
{
    public async Task PublishAsync(RegistryApplyResult result, ChangeContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);
        var body = new RegistryChangedBody(
            result.Revision,
            new RegistryChangedContext(context.UserId, context.Reason, context.CorrelationId),
            [.. result.Changes.Select(c => new RegistryChangedEntity(c.Entity, c.Id, KindName(c.Kind)))]);
        var origin = Guid.TryParse(context.UserId, out var user) ? new RecordOrigin(RecordOriginType.User, user) : RecordOrigin.System;
        var envelope = new LogEnvelope(
            Guid.CreateVersion7(timeProvider.GetUtcNow()),
            RecordKinds.RegistryChanged,
            timeProvider.UtcNowMs(),
            Src: null,
            P: 0,
            Dev: null,
            Srv: null,
            origin,
            RecordCorrelation.None,
            registry.SerializeBody(RecordKinds.RegistryChanged, body));
        try
        {
            await writer.AppendGlobalAsync(LogStream.Cfg, envelope, ct).ConfigureAwait(false);
        }
        catch (PipelineException ex)
        {
            PipelineLog.RegistryChangeNotPublished(logger, result.Revision, ex.Message);
        }
    }

    private static string KindName(ChangeKind kind) => kind switch
    {
        ChangeKind.Created => "created",
        ChangeKind.Updated => "updated",
        ChangeKind.Deleted => "deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown change kind."),
    };
}
