namespace Watchpoint.Storage.Contracts;

/// <summary>A module's configuration-store context (spec STO §6.2); created by <c>AddModuleStorage</c>.</summary>
public interface IModuleStorageRegistration
{
    /// <summary>Gets the module name, e.g. <c>Registry</c> (schema <c>wp_registry</c>).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Name fixed by spec STO §6.2.")]
    string Module { get; }

    /// <summary>Gets the provider-neutral context type.</summary>
    Type DbContextType { get; }

    /// <summary>Gets the provider-specific context type per provider id (each has its own migrations).</summary>
    IReadOnlyDictionary<string, Type> ProviderContexts { get; }

    /// <summary>Gets the migration order (ascending).</summary>
    int Order { get; }
}
