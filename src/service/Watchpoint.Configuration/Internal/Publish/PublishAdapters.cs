using Watchpoint.Configuration.Contracts;
using Watchpoint.Configuration.Internal.Storage;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Scheduling;

namespace Watchpoint.Configuration.Internal.Publish;

/// <summary><c>config.published</c> on <c>WP_CFG</c> through the pipeline's writer (spec CFG §7.1 step 6).</summary>
internal sealed class LogConfigPublishedSink : IConfigPublishedSink
{
    /// <summary>The record kind.</summary>
    public const string Kind = "config.published";

    private readonly IEventLogWriter _log;
    private readonly ILogRecordTypeRegistry _registry;

    public LogConfigPublishedSink(IEventLogWriter log, ILogRecordTypeRegistry registry)
    {
        _log = log;
        _registry = registry;
        registry.Register(Kind, 1, ConfigJsonContext.Default.ConfigPublishedBody);
    }

    public async ValueTask AppendAsync(ConfigPublishedBody body, UserId actor, DateTimeOffset at, CancellationToken ct)
    {
        var envelope = new LogEnvelope(Guid.CreateVersion7(at), Kind, at, Src: null, P: 0, Dev: null, Srv: null,
            new RecordOrigin(RecordOriginType.User, actor.Value), RecordCorrelation.None, _registry.SerializeBody(Kind, body));
        await _log.AppendGlobalAsync(LogStream.Cfg, envelope, ct).ConfigureAwait(false);
    }
}

/// <summary>Scheduled publish through SCH one-shot triggers (owner <c>config</c>).</summary>
internal sealed class SchedulingConfigScheduler(ITimeTriggerService triggers) : IConfigScheduler
{
    public IDisposable At(string key, DateTimeOffset when, Func<CancellationToken, ValueTask> callback) =>
        triggers.Register("config", key, new TriggerSpec.At(when), MissedPolicy.FireOnce, (_, ct) => callback(ct));
}

/// <summary>
/// The M2 rights before RGT (T3.04): configuration is for enabled administrators only. RGT registers its own
/// <see cref="IConfigurationAuthorizer"/>, which replaces this one.
/// </summary>
internal sealed class FallbackConfigurationAuthorizer(IUserDirectory users) : IConfigurationAuthorizer
{
    public ValueTask<bool> IsAllowedAsync(UserId user, PermissionCode permission, ConfigObjectRef? scope, CancellationToken ct) =>
        ValueTask.FromResult(users.Get(user).TryGetValue(out var found, out _) && found.Enabled && found.Administrator);
}
