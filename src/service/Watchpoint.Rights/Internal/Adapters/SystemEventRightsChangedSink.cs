using Microsoft.Extensions.Logging;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Rights.Contracts;

namespace Watchpoint.Rights.Internal.Adapters;

/// <summary>Publishes <c>rights.changed { userIds }</c> as a system event (WP_SYS) so the API can re-filter live subscriptions (spec RGT §6, API §8.6).</summary>
internal sealed class SystemEventRightsChangedSink(ISystemEventSink events, TimeProvider time, ILogger<SystemEventRightsChangedSink> logger) : IRightsChangedSink
{
    public const string Kind = "rights.changed";

    public void RightsChanged(IReadOnlyCollection<UserId> users)
    {
        var payload = new Dictionary<string, string?> { ["userIds"] = string.Join(',', users.Select(u => u.Value.ToString())) };
        _ = PublishAsync(new IdentitySystemEvent(Kind, IdentitySystemEventSeverity.Info, payload, time.GetUtcNow()));
    }

    private async Task PublishAsync(IdentitySystemEvent e)
    {
        try
        {
            await events.PublishAsync(e, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RightsLog.PublishFailed(logger, ex);
        }
    }
}
