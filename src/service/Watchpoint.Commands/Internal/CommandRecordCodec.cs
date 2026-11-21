using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Watchpoint.Commands.Contracts;
using Watchpoint.Kernel.Json;
using Watchpoint.Kernel.Results;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Commands.Internal;

/// <summary>Builds and reads the command lifecycle records (spec CMD §6, PIP §5.2–§5.3).</summary>
internal sealed class CommandRecordCodec
{
    public static readonly JsonSerializerOptions Json = WatchpointJson.Create(JsonProfile.Log, CommandsJsonContext.Default);

    private readonly ILogRecordTypeRegistry _registry;

    public CommandRecordCodec(ILogRecordTypeRegistry registry)
    {
        _registry = registry;
        Register(registry);
    }

    public ILogRecordTypeRegistry Registry => _registry;

    /// <summary>Registers every command kind (schema 1); registering again is harmless.</summary>
    public static void Register(ILogRecordTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(CommandRecordKinds.Requested, 1, Info<Command>());
        registry.Register(CommandRecordKinds.Rejected, 1, Info<CommandRejectedBody>());
        registry.Register(CommandRecordKinds.AwaitingApproval, 1, Info<CommandAwaitingApprovalBody>());
        registry.Register(CommandRecordKinds.Approved, 1, Info<CommandApprovalBody>());
        registry.Register(CommandRecordKinds.ApprovalRejected, 1, Info<CommandApprovalBody>());
        registry.Register(CommandRecordKinds.Queued, 1, Info<CommandQueuedBody>());
        registry.Register(CommandRecordKinds.Dispatched, 1, Info<CommandDispatchedBody>());
        registry.Register(CommandRecordKinds.Succeeded, 1, Info<CommandSucceededBody>());
        registry.Register(CommandRecordKinds.Failed, 1, Info<CommandFailedBody>());
        registry.Register(CommandRecordKinds.TimedOut, 1, Info<CommandIdBody>());
        registry.Register(CommandRecordKinds.Expired, 1, Info<CommandByBody>());
        registry.Register(CommandRecordKinds.Cancelled, 1, Info<CommandByBody>());
        registry.Register(CommandRecordKinds.Verified, 1, Info<CommandVerificationBody>());
        registry.Register(CommandRecordKinds.Unverified, 1, Info<CommandVerificationBody>());
    }

    public static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)Json.GetTypeInfo(typeof(T));

    /// <summary>A record of <paramref name="command"/>: in the target's partition, origin from the issuer, correlation of the command.</summary>
    public LogEnvelope Envelope<TBody>(string kind, Command command, TBody body, DateTimeOffset now) =>
        new(
            Guid.CreateVersion7(now),
            kind,
            now,
            Src: null,
            Partitioning.Of(command.Target),
            command.Target,
            Srv: null,
            Origin(command.Issuer),
            command.Correlation with { Command = command.Id },
            _registry.SerializeBody(kind, body));

    /// <summary>The rejection of a request that has no <c>command.requested</c> record.</summary>
    public LogEnvelope Rejection(CommandRejectedBody body, Watchpoint.Kernel.Ids.DeviceId target, RecordCorrelation? correlation, DateTimeOffset now) =>
        new(
            Guid.CreateVersion7(now),
            CommandRecordKinds.Rejected,
            now,
            Src: null,
            Partitioning.Of(target),
            target,
            Srv: null,
            body.Issuer is { } issuer ? Origin(issuer) : RecordOrigin.System,
            (correlation ?? RecordCorrelation.None) with { Command = body.Id },
            _registry.SerializeBody(CommandRecordKinds.Rejected, body));

    public T Body<T>(LogRecord record) => record.BodyAs<T>(_registry);

    public static RecordOrigin Origin(CommandIssuer issuer) => issuer switch
    {
        CommandIssuer.User u => new RecordOrigin(RecordOriginType.User, u.UserId.Value),
        CommandIssuer.Automation a => new RecordOrigin(RecordOriginType.Automation, a.AutomationId.Value),
        CommandIssuer.Script s => new RecordOrigin(RecordOriginType.Script, s.ScriptId.Value),
        CommandIssuer.Federation f => new RecordOrigin(RecordOriginType.Federation, f.LinkId.Value),
        _ => RecordOrigin.System,
    };

    /// <summary>An error as a record body member: invariant-culture text arguments and the field problems.</summary>
    public static CommandRecordError ToRecord(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var args = error.Args.ToDictionary(a => a.Key, a => Text(a.Value), StringComparer.Ordinal);
        var fields = error.FieldErrors.Count == 0 ? null : error.FieldErrors.Select(f => new CommandRecordFieldError(f.Path, f.Code.Value)).ToList();
        return new CommandRecordError(error.Code.Value, args, fields);
    }

    /// <summary>A failure from an error: refused by the system, key <c>errors.&lt;code&gt;</c>.</summary>
    public static CommandFailure ToFailure(CommandRecordError error) =>
        new(Integration.Abstractions.Model.FailureCategory.RefusedBySystem, "errors." + error.Code, error.Args, VendorCode: null);

    public static string? Text(object? value) => value switch
    {
        null => null,
        DateTimeOffset t => t.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
