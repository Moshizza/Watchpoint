namespace Watchpoint.Storage.Contracts;

/// <summary>Database object naming (spec STO §4.1); hides schemas vs table prefixes from modules.</summary>
public interface IStorageNaming
{
    /// <summary>Gets whether the provider has schemas.</summary>
    bool UsesSchemas { get; }

    /// <summary>The schema (or prefix) of a module or store: <c>wp_&lt;name&gt;</c> in lowercase.</summary>
    /// <param name="moduleOrStore">Module name, e.g. <c>Registry</c>, or store name, e.g. <c>state</c>.</param>
    /// <returns>E.g. <c>wp_registry</c>.</returns>
    string SchemaFor(string moduleOrStore);

    /// <summary>The (schema, table) pair to use for a table of a schema.</summary>
    /// <param name="schema">The schema from <see cref="SchemaFor"/>.</param>
    /// <param name="table">The table name.</param>
    /// <returns>With schemas: (<paramref name="schema"/>, <paramref name="table"/>); without: (<see langword="null"/>, <c>&lt;schema&gt;__&lt;table&gt;</c>).</returns>
    (string? Schema, string Table) TableName(string schema, string table);
}

/// <summary>The two naming strategies.</summary>
public static class StorageNaming
{
    /// <summary>Naming for providers with schemas (PostgreSQL, SQL Server).</summary>
    public static IStorageNaming Schemas { get; } = new Naming(usesSchemas: true);

    /// <summary>Naming for providers without schemas (table prefixes <c>&lt;schema&gt;__&lt;table&gt;</c>).</summary>
    public static IStorageNaming TablePrefixes { get; } = new Naming(usesSchemas: false);

    private sealed class Naming(bool usesSchemas) : IStorageNaming
    {
        public bool UsesSchemas => usesSchemas;

        public string SchemaFor(string moduleOrStore)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(moduleOrStore);
            if (!moduleOrStore.All(char.IsAsciiLetterOrDigit))
            {
                throw new ArgumentException($"'{moduleOrStore}' must contain only ASCII letters and digits.", nameof(moduleOrStore));
            }

            return "wp_" + moduleOrStore.ToLowerInvariant();
        }

        public (string? Schema, string Table) TableName(string schema, string table) =>
            usesSchemas ? (schema, table) : (null, $"{schema}__{table}");
    }
}
