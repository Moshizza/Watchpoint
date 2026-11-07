using System.Data.Common;

namespace Watchpoint.Storage.Contracts;

/// <summary>Database, provider and connections of each store (spec STO §6.1).</summary>
public interface IStoreConnectionFactory
{
    /// <summary>The database a store is mapped to.</summary>
    /// <param name="store">The store.</param>
    /// <returns>The database.</returns>
    DatabaseDefinition GetDatabase(StoreKind store);

    /// <summary>The provider of a store.</summary>
    /// <param name="store">The store.</param>
    /// <returns>The provider.</returns>
    IStorageProvider GetProvider(StoreKind store);

    /// <summary>Opens a pooled connection to the database of a store.</summary>
    /// <param name="store">The store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The open connection; the caller disposes it.</returns>
    ValueTask<DbConnection> OpenConnectionAsync(StoreKind store, CancellationToken ct);
}

/// <summary>Resolves database passwords. Replaced by the secrets module (SEC, T1.05); the default only reads <c>PasswordDev</c>.</summary>
public interface IDatabasePasswordProvider
{
    /// <summary>The password of a database, or <see langword="null"/> for none (e.g. trust/peer authentication).</summary>
    /// <param name="db">The database.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The password.</returns>
    ValueTask<string?> GetPasswordAsync(DatabaseDefinition db, CancellationToken ct);
}
