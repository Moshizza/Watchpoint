using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Rights.Contracts;

/// <summary>The permission catalogue (spec RGT §4).</summary>
public interface IPermissionCatalogue
{
    /// <summary>Gets every registered permission.</summary>
    IReadOnlyList<PermissionDefinition> All { get; }

    /// <summary>Gets a permission by code.</summary>
    /// <param name="code">The exact code.</param>
    /// <returns>The definition, or <c>rights.permission.unknown</c>.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Name fixed by spec RGT §4.")]
    Result<PermissionDefinition> Get(PermissionCode code);

    /// <summary>Whether a grant pattern covers a requested code (<c>commands.openable.*</c> covers <c>commands.openable.open</c>; <c>*</c> covers all).</summary>
    /// <param name="grantPattern">The pattern or code of the grant.</param>
    /// <param name="requested">The requested code.</param>
    /// <returns><see langword="true"/> when covered.</returns>
    bool Matches(PermissionCode grantPattern, PermissionCode requested);

    /// <summary>Registers (or replaces) a permission; modules call it at phase Catalogue.</summary>
    /// <param name="definition">The definition.</param>
    void Register(PermissionDefinition definition);
}
