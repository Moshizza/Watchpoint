namespace Watchpoint.Storage.Contracts;

/// <summary>The four stores of a node (spec STO §4); each is mapped to a database by settings.</summary>
public enum StoreKind
{
    /// <summary>Configuration entities (one schema per module, EF Core).</summary>
    Configuration,

    /// <summary>State snapshots (STA, ALM, CMD).</summary>
    State,

    /// <summary>Property history (HIS).</summary>
    History,

    /// <summary>Audit trail (AUD).</summary>
    Audit,
}

/// <summary>Binding of a store to a provider and a database definition.</summary>
/// <param name="Kind">The store.</param>
/// <param name="ProviderId">Provider id, e.g. <c>postgres</c>.</param>
/// <param name="DatabaseKey">Key of the database definition (<c>Database:Databases:&lt;key&gt;</c>).</param>
public sealed record StoreBinding(StoreKind Kind, string ProviderId, string DatabaseKey);
