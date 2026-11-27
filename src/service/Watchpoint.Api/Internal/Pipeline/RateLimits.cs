using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Watchpoint.Api.Contracts;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Api.Internal.Pipeline;

/// <summary>
/// Rate limits (spec API §4.2): a global token bucket per remote IP (anonymous) or per session (authenticated), and the
/// <c>heavy</c> concurrency policy per session. Rejected → 429 <c>common.rate_limited</c> with <c>Retry-After</c>.
/// Runs after authentication, which gives the partition key.
/// </summary>
internal static class RateLimits
{
    public static void Configure(RateLimiterOptions options, ApiRateLimits limits)
    {
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                && !context.Request.Path.StartsWithSegments("/hub", StringComparison.OrdinalIgnoreCase))
            {
                return RateLimitPartition.GetNoLimiter("unlimited");
            }

            var user = context.Features.Get<ICurrentUser>();
            if (user is { IsAuthenticated: true })
            {
                return RateLimitPartition.GetTokenBucketLimiter("s:" + user.SessionId, _ => Bucket(limits.AuthenticatedPerSecond, limits.AuthenticatedBurst));
            }

            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetTokenBucketLimiter("ip:" + ip, _ => Bucket(limits.AnonymousPerSecond, limits.AnonymousBurst));
        });

        options.AddPolicy(EndpointConventions.HeavyPolicy, context =>
        {
            var user = context.Features.Get<ICurrentUser>();
            var key = user is { IsAuthenticated: true } ? "s:" + user.SessionId : "ip:" + context.Connection.RemoteIpAddress;
            return RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = limits.HeavyConcurrency,
                QueueLimit = limits.HeavyQueue,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
        });

        options.OnRejected = async (rejected, ct) =>
        {
            var retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds)) : 1;
            rejected.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            await ProblemDetailsWriter.WriteAsync(rejected.HttpContext, CommonErrors.RateLimited(retryAfter)).ConfigureAwait(false);
        };
    }

    private static TokenBucketRateLimiterOptions Bucket(int perSecond, int burst) => new()
    {
        TokenLimit = burst,
        TokensPerPeriod = perSecond,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        AutoReplenishment = true,
        QueueLimit = 0,
    };
}
