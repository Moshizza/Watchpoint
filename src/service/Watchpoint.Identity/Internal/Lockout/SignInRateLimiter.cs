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
    private int _calls;

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

        // Logins longer than U1 allows can never exist: they share one bucket, so that the key (and the memory
        // behind it) is bounded whatever an anonymous caller sends.
        var loginKey = string.IsNullOrWhiteSpace(login) ? null : login.Trim().ToLowerInvariant();
        if (loginKey is { Length: > 64 })
        {
            loginKey = "#invalid";
        }

        lock (_gate)
        {
            if (++_calls % 256 == 0)
            {
                Sweep(_ipAttempts, cutoff);
                Sweep(_loginAttempts, cutoff);
            }

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

            if (loginKey is not null)
            {
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

            if (loginKey is not null)
            {
                _loginAttempts[loginKey].Add(now);
            }

            return true;
        }
    }

    /// <summary>Gets the number of keys held (tests).</summary>
    internal int TrackedKeys
    {
        get
        {
            lock (_gate)
            {
                return _ipAttempts.Count + _loginAttempts.Count;
            }
        }
    }

    // Drops the keys with no attempt left in the window; without it every login or address ever tried stays for ever.
    private static void Sweep(ConcurrentDictionary<string, List<DateTimeOffset>> attempts, DateTimeOffset cutoff)
    {
        foreach (var pair in attempts)
        {
            pair.Value.RemoveAll(t => t < cutoff);
            if (pair.Value.Count == 0)
            {
                attempts.TryRemove(pair.Key, out _);
            }
        }
    }
}
