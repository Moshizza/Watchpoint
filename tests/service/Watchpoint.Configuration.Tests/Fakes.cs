using System.Runtime.CompilerServices;
using System.Text.Json;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Configuration.Tests;

/// <summary>Live documents of all types, in memory.</summary>
internal sealed class FakeSource : IConfigDocumentSource
{
    public Dictionary<ConfigObjectRef, ConfigDocument> Documents { get; } = [];

    public int Loads { get; private set; }

    public void Put(string type, Guid id, long version, string json) =>
        Documents[new ConfigObjectRef(type, id)] = new ConfigDocument(new ConfigObjectRef(type, id), version, JsonDocument.Parse(json).RootElement.Clone());

    public Task<ConfigDocument?> LoadAsync(string type, Guid id, CancellationToken ct)
    {
        Loads++;
        return Task.FromResult(Documents.GetValueOrDefault(new ConfigObjectRef(type, id)));
    }

    public async IAsyncEnumerable<ConfigDocument> ListAsync(string type, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var document in Documents.Values.Where(d => d.Ref.Type == type).OrderBy(d => d.Ref.Id))
        {
            yield return document;
        }

        await Task.CompletedTask;
    }
}

/// <summary>Allows everything except what a test denies (permission, optionally for one object).</summary>
internal sealed class FakeAuthorizer : IConfigurationAuthorizer
{
    public HashSet<(UserId User, string Permission)> Denied { get; } = [];

    public HashSet<(string Permission, ConfigObjectRef Object)> DeniedObjects { get; } = [];

    public ValueTask<bool> IsAllowedAsync(UserId user, PermissionCode permission, ConfigObjectRef? scope, CancellationToken ct) =>
        ValueTask.FromResult(!Denied.Contains((user, permission.Value)) && !(scope is { } s && DeniedObjects.Contains((permission.Value, s))));
}

/// <summary>A vault in memory.</summary>
internal sealed class FakeVault : ISecretVault
{
    public Dictionary<SecretId, (string Name, SecretOwner Owner, string Value)> Secrets { get; } = [];

    public Task<Result<SecretId>> CreateAsync(string name, SecretKind kind, SecretOwner owner, ReadOnlyMemory<byte> value, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var id = SecretId.New();
        Secrets[id] = (name, owner, System.Text.Encoding.UTF8.GetString(value.Span));
        return Task.FromResult<Result<SecretId>>(id);
    }

    public Task<Result> DeleteAsync(SecretId id, SecretOwner caller, CancellationToken ct) =>
        Task.FromResult(Secrets.Remove(id) ? Result.Success() : Result.Failure(Error.Create(ErrorKind.NotFound, "secrets.not_found")));

    public Task<Result> ReplaceAsync(SecretId id, SecretOwner caller, ReadOnlyMemory<byte> value, DateTimeOffset? expiresAt, CancellationToken ct) => throw new NotSupportedException();

    public Task<Result<SecretValue>> ReadAsync(SecretId id, SecretOwner caller, string purpose, CancellationToken ct) => throw new NotSupportedException();

    public Task<Result<SecretMetadata>> GetMetadataAsync(SecretId id, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<SecretMetadata>> ListAsync(SecretOwner? owner, CancellationToken ct) => throw new NotSupportedException();

    public Task<Result<int>> RewrapAllAsync(CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class FakePublisher : IPublishService
{
    public List<(ChangeSet ChangeSet, UserId Actor, string? Comment)> Published { get; } = [];

    public Task<Result<long>> PublishAsync(ChangeSet changeSet, UserId actor, string? comment, CancellationToken ct)
    {
        Published.Add((changeSet, actor, comment));
        return Task.FromResult<Result<long>>(41L + Published.Count);
    }
}

internal sealed class FakeLicence : ILicenceProjection
{
    public int Allowed { get; set; } = int.MaxValue;

    public ValueTask<IReadOnlyList<Problem>> ProjectAsync(IReadOnlyList<DraftEntry> entries, IDraftView view, CancellationToken ct)
    {
        var projected = entries.Count(e => e.Ref.Type == "registry.device" && e.Operation == EntryOperation.Create);
        IReadOnlyList<Problem> problems = projected <= Allowed
            ? []
            : [new Problem(ProblemSeverity.Error, ConfigurationErrors.LicenceQuotaExceeded, new Dictionary<string, object?> { ["allowed"] = Allowed, ["projected"] = projected, ["unit"] = "devices" }, null, null, [])];
        return ValueTask.FromResult(problems);
    }
}
