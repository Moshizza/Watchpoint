using System.Data.Common;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Internal;

internal sealed class StoreConnectionFactory(StoreMapHolder holder) : IStoreConnectionFactory
{
    public DatabaseDefinition GetDatabase(StoreKind store) => holder.Map.Database(store);

    public IStorageProvider GetProvider(StoreKind store) => holder.Map.Provider(store);

    public ValueTask<DbConnection> OpenConnectionAsync(StoreKind store, CancellationToken ct) =>
        GetProvider(store).OpenConnectionAsync(GetDatabase(store), ct);
}

/// <summary>Default password source until the secrets module exists: <c>PasswordDev</c> (Development only, enforced by settings validation).</summary>
internal sealed class DevelopmentPasswordProvider : IDatabasePasswordProvider
{
    public ValueTask<string?> GetPasswordAsync(DatabaseDefinition db, CancellationToken ct) => ValueTask.FromResult(db.PasswordDev);
}
