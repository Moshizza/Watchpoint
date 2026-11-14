using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal.Storage;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Identity.Internal.Lockout;

/// <summary>Lockout management service with sliding failure window and progressive doubling (spec IDN §6.4).</summary>
internal sealed class LockoutService : IDisposable
{
    private readonly IDbContextFactory<IdentityDbContext> _dbFactory;
    private readonly TimeProvider _clock;
    private readonly LockoutOptions _options;
    private readonly ILogger<LockoutService>? _logger;

    // Read-modify-write of the failure list: without it concurrent wrong passwords overwrite each other and the
    // threshold is never reached.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LockoutService(
        IDbContextFactory<IdentityDbContext> dbFactory,
        TimeProvider clock,
        LockoutOptions? options = null,
        ILogger<LockoutService>? logger = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new LockoutOptions();
        _logger = logger;
    }

    /// <summary>Checks whether a user is currently locked out.</summary>
    public async Task<(bool IsLocked, DateTimeOffset? LockedUntil)> CheckLockoutAsync(UserId userId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var lockout = await db.Lockouts.FindAsync([userId], ct).ConfigureAwait(false);

        if (lockout?.LockedUntil is { } until && until > now)
        {
            return (true, until);
        }

        return (false, null);
    }

    /// <summary>Records a failed sign-in attempt and calculates progressive lockouts (spec IDN §6.4).</summary>
    public async Task<(bool NewlyLocked, DateTimeOffset? LockedUntil)> RecordFailureAsync(UserId userId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RecordFailureCoreAsync(userId, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(bool NewlyLocked, DateTimeOffset? LockedUntil)> RecordFailureCoreAsync(UserId userId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var lockout = await db.Lockouts.FindAsync([userId], ct).ConfigureAwait(false);

        if (lockout is null)
        {
            lockout = new LockoutEntity
            {
                UserId = userId,
                FailuresJson = "[]",
                LockedUntil = null,
                LockCount24h = 0,
            };
            db.Lockouts.Add(lockout);
        }

        // "Each further lock in 24 h doubles": a day without lock starts again at the base duration.
        if (lockout.LockedUntil is { } lastLock && now - lastLock > TimeSpan.FromHours(24))
        {
            lockout.LockCount24h = 0;
        }

        // Clean up failures outside sliding window (15 min)
        var failures = (JsonSerializer.Deserialize<List<DateTimeOffset>>(lockout.FailuresJson) ?? [])
            .Where(t => now - t <= _options.Window)
            .ToList();

        failures.Add(now);
        lockout.FailuresJson = JsonSerializer.Serialize(failures);

        // Check if failures threshold reached
        if (failures.Count >= _options.Threshold)
        {
            // Reset sliding failures for next cycle
            lockout.FailuresJson = "[]";
            lockout.LockCount24h++;

            // Progressive doubling: 15 min, 30 min, 1h, 2h... up to 24h
            var multiplier = Math.Pow(2, Math.Min(lockout.LockCount24h - 1, 16));
            var durationTicks = (long)(_options.BaseDuration.Ticks * multiplier);
            var duration = TimeSpan.FromTicks(Math.Min(durationTicks, _options.MaxDuration.Ticks));

            var until = now + duration;
            lockout.LockedUntil = until;

            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            if (_logger is not null)
            {
                IdentityLog.AccountLocked(_logger, userId.ToString(), until.ToString("o"));
            }

            return (true, until);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (false, null);
    }

    /// <summary>Resets the failure counter upon successful sign-in.</summary>
    public async Task ResetFailuresAsync(UserId userId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var lockout = await db.Lockouts.FindAsync([userId], ct).ConfigureAwait(false);
        if (lockout is not null)
        {
            lockout.FailuresJson = "[]";
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Clears lockout and unlocks user account (administrator action).</summary>
    public async Task UnlockAsync(UserId userId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var lockout = await db.Lockouts.FindAsync([userId], ct).ConfigureAwait(false);
        if (lockout is not null)
        {
            lockout.LockedUntil = null;
            lockout.FailuresJson = "[]";
            lockout.LockCount24h = 0;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
