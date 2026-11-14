using System.Collections.Concurrent;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Identity.Tests;

/// <summary>In-memory vault with the owner rule of SEC §5 (read, replace, delete only by the owner).</summary>
internal sealed class FakeSecretVault : ISecretVault
{
    private readonly ConcurrentDictionary<SecretId, (string Name, SecretKind Kind, SecretOwner Owner, byte[] Value, DateTimeOffset? ExpiresAt, int Version)> _secrets = new();

    public bool Unavailable { get; set; }

    public int Count => _secrets.Count;

    public IReadOnlyList<(SecretId Id, string Name, SecretOwner Owner, byte[] Value)> All => _secrets.Select(p => (p.Key, p.Value.Name, p.Value.Owner, p.Value.Value)).ToList();

    public List<(string Purpose, SecretOwner Caller)> Reads { get; } = [];

    public Task<Result<SecretId>> CreateAsync(string name, SecretKind kind, SecretOwner owner, ReadOnlyMemory<byte> value, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        if (Unavailable)
        {
            return Task.FromResult<Result<SecretId>>(Error.Unavailable("secrets.unavailable"));
        }

        var id = SecretId.New();
        _secrets[id] = (name, kind, owner, value.ToArray(), expiresAt, 1);
        return Task.FromResult<Result<SecretId>>(id);
    }

    public Task<Result> ReplaceAsync(SecretId id, SecretOwner caller, ReadOnlyMemory<byte> value, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        if (!_secrets.TryGetValue(id, out var s))
        {
            return Task.FromResult<Result>(Error.NotFound("secrets.not_found"));
        }

        if (!Owns(s.Owner, caller))
        {
            return Task.FromResult<Result>(Error.Forbidden("secrets.access.denied"));
        }

        _secrets[id] = (s.Name, s.Kind, s.Owner, value.ToArray(), expiresAt, s.Version + 1);
        return Task.FromResult(Result.Success());
    }

    public Task<Result<SecretValue>> ReadAsync(SecretId id, SecretOwner caller, string purpose, CancellationToken ct)
    {
        Reads.Add((purpose, caller));
        if (Unavailable)
        {
            return Task.FromResult<Result<SecretValue>>(Error.Unavailable("secrets.unavailable"));
        }

        if (!_secrets.TryGetValue(id, out var s))
        {
            return Task.FromResult<Result<SecretValue>>(Error.NotFound("secrets.not_found"));
        }

        if (!Owns(s.Owner, caller))
        {
            return Task.FromResult<Result<SecretValue>>(Error.Forbidden("secrets.access.denied"));
        }

        return Task.FromResult<Result<SecretValue>>(SecretValue.Copy(s.Value));
    }

    public Task<Result> DeleteAsync(SecretId id, SecretOwner caller, CancellationToken ct)
    {
        if (!_secrets.TryGetValue(id, out var s))
        {
            return Task.FromResult<Result>(Error.NotFound("secrets.not_found"));
        }

        if (!Owns(s.Owner, caller))
        {
            return Task.FromResult<Result>(Error.Forbidden("secrets.access.denied"));
        }

        _secrets.TryRemove(id, out _);
        return Task.FromResult(Result.Success());
    }

    public Task<Result<SecretMetadata>> GetMetadataAsync(SecretId id, CancellationToken ct) =>
        Task.FromResult<Result<SecretMetadata>>(_secrets.TryGetValue(id, out var s)
            ? new SecretMetadata(id, s.Name, s.Kind, s.Owner, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, s.ExpiresAt, s.Version)
            : Error.NotFound("secrets.not_found"));

    public Task<IReadOnlyList<SecretMetadata>> ListAsync(SecretOwner? owner, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SecretMetadata>>(_secrets
            .Where(p => owner is null || (p.Value.Owner.Module == owner.Module && (owner.EntityId is null || p.Value.Owner.EntityId == owner.EntityId)))
            .Select(p => new SecretMetadata(p.Key, p.Value.Name, p.Value.Kind, p.Value.Owner, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, p.Value.ExpiresAt, p.Value.Version))
            .ToList());

    public Task<Result<int>> RewrapAllAsync(CancellationToken ct) => Task.FromResult<Result<int>>(0);

    private static bool Owns(SecretOwner owner, SecretOwner caller) =>
        string.Equals(owner.Module, caller.Module, StringComparison.Ordinal) && (owner.EntityId is null || owner.EntityId == caller.EntityId);
}

internal sealed class RecordingAuditSink : IIdentityAuditSink
{
    private readonly ConcurrentQueue<IdentityAuditRecord> _records = new();

    public IReadOnlyList<IdentityAuditRecord> Records => [.. _records];

    public IEnumerable<IdentityAuditRecord> Of(string action) => Records.Where(r => r.Action == action);

    public ValueTask RecordAsync(IdentityAuditRecord record, CancellationToken ct)
    {
        _records.Enqueue(record);
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingSystemEventSink : ISystemEventSink
{
    private readonly ConcurrentQueue<IdentitySystemEvent> _events = new();

    public IReadOnlyList<IdentitySystemEvent> Events => [.. _events];

    public IEnumerable<IdentitySystemEvent> Of(string kind) => Events.Where(e => e.Kind == kind);

    public ValueTask PublishAsync(IdentitySystemEvent systemEvent, CancellationToken ct)
    {
        _events.Enqueue(systemEvent);
        return ValueTask.CompletedTask;
    }
}

/// <summary>RGT stand-in: administrators get everything; other users get what the test assigns.</summary>
internal sealed class FakePermissionProvider : IPermissionSummaryProvider
{
    public ConcurrentDictionary<UserId, IReadOnlyList<string>> Granted { get; } = new();

    public ValueTask<IReadOnlyList<string>> GetEffectivePermissionsAsync(User user, CancellationToken ct)
    {
        if (Granted.TryGetValue(user.Id, out var granted))
        {
            return ValueTask.FromResult(granted);
        }

        return ValueTask.FromResult<IReadOnlyList<string>>(user.Administrator && user.Enabled ? ["*"] : []);
    }
}
