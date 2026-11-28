using Watchpoint.Kernel.Ids;
using Watchpoint.Rights.Contracts;

namespace Watchpoint.Rights.Internal.Catalogue;

/// <summary>Expands a grant permission (code or pattern) to the catalogue permissions it covers.</summary>
internal sealed class PermissionCatalogueAccess(IPermissionCatalogue catalogue)
{
    public IReadOnlyList<PermissionDefinition> Expand(PermissionCode pattern) =>
        [.. catalogue.All.Where(d => catalogue.Matches(pattern, d.Code))];
}
