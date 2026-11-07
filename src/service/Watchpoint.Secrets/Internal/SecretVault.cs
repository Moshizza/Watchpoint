using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Secrets.Internal;

/// <summary>The vault (spec SEC §5, §6.1, §8): envelope-encrypted values in <c>wp_secrets.secrets</c>, owner checks, audit.</summary>
internal sealed class SecretVault(
    IDbContextFactory<SecretsDbContext> contexts,
    MasterKeyAccessor keys,
    SecretsOptions options,
    ISecretsAuditSink audit,
    SecretsMetrics metrics,
    TimeProvider clock,
    ILogger<SecretVault> logger) : ISecretVault
{
    /// <summary><c>last_accessed_at</c> is written at most once per this interval per secret.</summary>
    public static readonly TimeSpan AccessWriteInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<(Guid Secret, string Module, Guid? Entity), (DateTimeOffset Hour, int Count)> _reads = new();

    public async Task<Result<SecretId>> CreateAsync(string name, SecretKind kind, SecretOwner owner, ReadOnlyMemory<byte> value,
        DateTimeOffset? expiresAt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(owner);
        if (value.Length > options.MaxValueBytes)
        {
            return SecretsErrors.ValueTooLarge(options.MaxValueBytes);
        }

        var id = SecretId.New();
        var record = new SecretRecord { Id = id.Value, Name = name, Kind = kind.ToString(), OwnerModule = owner.Module, OwnerEntity = owner.EntityId, Version = 1, ExpiresAt = expiresAt };
        Seal(record, value.Span);
        await using var context = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        context.Secrets.Add(record);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        Audit("create", id, owner, 1);
        return id;
    }

    public async Task<Result> ReplaceAsync(SecretId id, SecretOwner caller, ReadOnlyMemory<byte> value, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (value.Length > options.MaxValueBytes)
        {
            return SecretsErrors.ValueTooLarge(options.MaxValueBytes);
        }

        await using var context = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await context.Secrets.AsTracking().SingleOrDefaultAsync(s => s.Id == id.Value, ct).ConfigureAwait(false);
        if (record is null)
        {
            return SecretsErrors.NotFound(id.Value.ToString("D"));
        }

        if (Denied(record, caller) is { } denied)
        {
            return denied;
        }

        record.Version++;
        record.ExpiresAt = expiresAt;
        Seal(record, value.Span);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        Audit("replace", id, caller, 1);
        return Result.Success();
    }

    public async Task<Result<SecretValue>> ReadAsync(SecretId id, SecretOwner caller, string purpose, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var context = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await context.Secrets.SingleOrDefaultAsync(s => s.Id == id.Value, ct).ConfigureAwait(false);
        if (record is null)
        {
            return SecretsErrors.NotFound(id.Value.ToString("D"));
        }

        if (Denied(record, caller) is { } denied)
        {
            return denied;
        }

        var value = EnvelopeCipher.Decrypt(record.Id, record.Version, ToEncrypted(record), keys.Ring);
        if (!value.IsSuccess)
        {
            SecretsLog.IntegrityFailed(logger, id.Value.ToString("D"));
            return value;
        }

        metrics.Read(caller.Module);
        SecretsLog.SecretRead(logger, id.Value, caller.Module, purpose);
        CountRead(id, caller);
        var now = clock.GetUtcNow();
        if (record.LastAccessedAt is not { } last || now - last >= AccessWriteInterval)
        {
            // Direct update: no row_version change, no updated_at change (access is not a modification).
            await context.Secrets.Where(s => s.Id == record.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastAccessedAt, now), ct).ConfigureAwait(false);
        }

        return value;
    }

    public async Task<Result> DeleteAsync(SecretId id, SecretOwner caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var context = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await context.Secrets.AsTracking().SingleOrDefaultAsync(s => s.Id == id.Value, ct).ConfigureAwait(false);
        if (record is null)
        {
            return SecretsErrors.NotFound(id.Value.ToString("D"));
        }

        if (Denied(record, caller) is { } denied)
        {
            return denied;
        }

        context.Secrets.Remove(record);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        Audit("delete", id, caller, 1);
        return Result.Success();
    }

    public async Task<Result<SecretMetadata>> GetMetadataAsync(SecretId id, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await context.Secrets.SingleOrDefaultAsync(s => s.Id == id.Value, ct).ConfigureAwait(false);
        return record is null ? SecretsErrors.NotFound(id.Value.ToString("D")) : ToMetadata(record);
    }

    public async Task<IReadOnlyList<SecretMetadata>> ListAsync(SecretOwner? owner, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = context.Secrets.AsQueryable();
        if (owner is not null)
        {
            query = query.Where(s => s.OwnerModule == owner.Module);
            if (owner.EntityId is { } entity)
            {
                query = query.Where(s => s.OwnerEntity == entity);
            }
        }

        var records = await query.OrderBy(s => s.Name).ThenBy(s => s.Id).ToListAsync(ct).ConfigureAwait(false);
        return [.. records.Select(ToMetadata)];
    }

    public async Task<Result<int>> RewrapAllAsync(CancellationToken ct)
    {
        var ring = keys.Ring;
        await using var context = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var stale = await context.Secrets.AsTracking().Where(s => s.KeyId != ring.Current.KeyId).ToListAsync(ct).ConfigureAwait(false);
        foreach (var record in stale)
        {
            var rewrapped = EnvelopeCipher.Rewrap(record.Id, record.Version, ToEncrypted(record), ring);
            if (rewrapped is null)
            {
                SecretsLog.IntegrityFailed(logger, record.Id.ToString("D"));
                return SecretsErrors.IntegrityFailed(record.Id.ToString("D"));
            }

            (record.KeyId, record.WrappedDek, record.DekNonce) = (rewrapped.KeyId, rewrapped.WrappedDek, rewrapped.DekNonce);
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        SecretsLog.Rewrapped(logger, stale.Count, ring.Current.KeyId);
        return stale.Count;
    }

    /// <summary>Writes the aggregated read audit records of past hours (called hourly and on shutdown).</summary>
    public void FlushReadAudit(bool all)
    {
        var hour = Hour(clock.GetUtcNow());
        foreach (var (key, (start, count)) in _reads)
        {
            if ((all || start < hour) && _reads.TryRemove(key, out var removed))
            {
                audit.Record(new SecretAuditRecord("read", SecretId.From(key.Secret), key.Module, key.Entity, removed.Hour, removed.Count));
            }
        }
    }

    private void CountRead(SecretId id, SecretOwner caller)
    {
        var hour = Hour(clock.GetUtcNow());
        var key = (id.Value, caller.Module, caller.EntityId);
        var previous = _reads.TryGetValue(key, out var p) ? p : default;
        if (previous.Count > 0 && previous.Hour < hour)
        {
            audit.Record(new SecretAuditRecord("read", id, caller.Module, caller.EntityId, previous.Hour, previous.Count));
            previous = default;
        }

        _reads[key] = (hour, previous.Count + 1);
    }

    private Error? Denied(SecretRecord record, SecretOwner caller)
    {
        var allowed = string.Equals(record.OwnerModule, caller.Module, StringComparison.Ordinal)
            && (record.OwnerEntity is null || record.OwnerEntity == caller.EntityId);
        if (allowed)
        {
            return null;
        }

        var id = SecretId.From(record.Id);
        metrics.Denied();
        SecretsLog.AccessDenied(logger, record.Id, caller.Module);
        Audit("denied", id, caller, 1);
        return SecretsErrors.AccessDenied(id);
    }

    private void Audit(string action, SecretId id, SecretOwner caller, int count) =>
        audit.Record(new SecretAuditRecord(action, id, caller.Module, caller.EntityId, clock.GetUtcNow(), count));

    private void Seal(SecretRecord record, ReadOnlySpan<byte> value)
    {
        var encrypted = EnvelopeCipher.Encrypt(record.Id, record.Version, value, keys.Ring.Current);
        (record.KeyId, record.WrappedDek, record.DekNonce, record.Ciphertext, record.Nonce) =
            (encrypted.KeyId, encrypted.WrappedDek, encrypted.DekNonce, encrypted.Ciphertext, encrypted.Nonce);
    }

    private static EncryptedValue ToEncrypted(SecretRecord r) => new(r.KeyId.Trim(), r.WrappedDek, r.DekNonce, r.Ciphertext, r.Nonce);

    private static SecretMetadata ToMetadata(SecretRecord r) =>
        new(SecretId.From(r.Id), r.Name, Enum.Parse<SecretKind>(r.Kind), new SecretOwner(r.OwnerModule, r.OwnerEntity), r.CreatedAt, r.UpdatedAt,
            r.LastAccessedAt, r.ExpiresAt, r.Version);

    private static DateTimeOffset Hour(DateTimeOffset t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, TimeSpan.Zero);
}
