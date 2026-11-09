using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Documents;

namespace Watchpoint.Model.Contracts;

/// <summary>Identifies a specific version of a device or server type.</summary>
/// <param name="Id">The type identifier (e.g. <c>ac.door</c>).</param>
/// <param name="Version">The semantic version of the type.</param>
public sealed record TypeRef(TypeId Id, SemVersion Version)
{
    /// <inheritdoc />
    public override string ToString() => $"{Id}@{Version}";
}
