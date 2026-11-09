using System.Collections.Concurrent;
using System.Net;
using Watchpoint.Identity.Contracts;

namespace Watchpoint.Identity.Internal.Lockout;

/// <summary>Sliding window rate limiter for sign-in attempts per IP and per login (spec IDN §6.1).</summary>
internal sealed class SignInRateLimiter
{
    private readonly TimeProvider _clock;
    private readonly SignInRateLimiterOptions _options;

    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _ipAttempts = new();
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _loginAttempts = new();
    private readonly Lock _gate = new();

    public SignInRateLimiter(TimeProvider clock, SignInRateLimiterOptions? options = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new SignInRateLimiterOptions();
    }

    /// <summary>Attempts to admit a sign-in request based on remote IP and login rates.</summary>
    public bool TryAcquire(IPAddress? remoteIp, string login)
    {
        var now = _clock.GetUtcNow();
        var cutoff = now - _options.Window;

        lock (_gate)
        {
            if (remoteIp is not null)
            {
                var ipKey = remoteIp.ToString();
                var attempts = _ipAttempts.GetOrAdd(ipKey, _ => []);
                attempts.RemoveAll(t => t < cutoff);
                if (attempts.Count >= _options.RatePerIp)
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(login))
            {
                var loginKey = login.Trim().ToLowerInvariant();
                var attempts = _loginAttempts.GetOrAdd(loginKey, _ => []);
                attempts.RemoveAll(t => t < cutoff);
                if (attempts.Count >= _options.RatePerLogin)
                {
                    return false;
                }
            }

            // Record attempt
            if (remoteIp is not null)
            {
                _ipAttempts[remoteIp.ToString()].Add(now);
            }

            if (!string.IsNullOrWhiteSpace(login))
            {
                _loginAttempts[login.Trim().ToLowerInvariant()].Add(now);
            }

            return true;
        }
    }
}
