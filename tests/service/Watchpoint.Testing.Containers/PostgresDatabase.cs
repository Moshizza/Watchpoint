namespace Watchpoint.Testing.Containers;

/// <summary>A database dedicated to one test class; disposing it drops the database (<c>WITH (FORCE)</c>).</summary>
public sealed class PostgresDatabase : IAsyncDisposable
{
    private readonly PostgresContainerFixture _fixture;
    private int _disposed;

    internal PostgresDatabase(PostgresContainerFixture fixture, string name, string connectionString)
    {
        _fixture = fixture;
        Name = name;
        ConnectionString = connectionString;
    }

    /// <summary>Gets the database name (<c>t_&lt;hash&gt;_&lt;unique&gt;</c>).</summary>
    public string Name { get; }

    /// <summary>Gets the connection string of the database.</summary>
    public string ConnectionString { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _fixture.DropDatabaseAsync(Name, ConnectionString).ConfigureAwait(false);
        }
    }
}
