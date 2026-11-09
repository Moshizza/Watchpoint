using System.Text.Json;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Model.Documents;

namespace Watchpoint.Model.Contracts;

/// <summary>Query filters for querying device types from the catalogue.</summary>
/// <param name="Role">Optional filter by role (Device or Server).</param>
/// <param name="Category">Optional filter by category.</param>
/// <param name="Source">Optional filter by source ("standard" or package id).</param>
/// <param name="Capability">Optional filter by capability presence.</param>
public sealed record TypeQuery(
    TypeRole? Role = null,
    Category? Category = null,
    string? Source = null,
    CapabilityId? Capability = null);

/// <summary>Package document payload provided during package registration (spec MOD §4.2).</summary>
/// <param name="Capabilities">Capability documents defined in the package.</param>
/// <param name="Types">Device type documents defined in the package.</param>
/// <param name="Translations">Localized translation JSON documents (keyed by culture code, e.g. "en").</param>
/// <param name="Symbols">SVG symbol contents (keyed by "&lt;symbolId&gt;/&lt;state&gt;.svg").</param>
public sealed record PackageTypeDocuments(
    IReadOnlyList<CapabilityDocument> Capabilities,
    IReadOnlyList<DeviceTypeDocument> Types,
    IReadOnlyDictionary<string, JsonDocument> Translations,
    IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Symbols);

/// <summary>Details of added types and capabilities following a catalogue change.</summary>
/// <param name="Added">Added type references.</param>
/// <param name="AddedCapabilities">Added capability identifiers.</param>
public sealed record CatalogChange(IReadOnlyList<TypeRef> Added, IReadOnlyList<CapabilityId> AddedCapabilities);

/// <summary>Event arguments raised when the type catalogue updates atomically.</summary>
/// <param name="Change">The change details.</param>
/// <param name="Revision">The new catalogue revision.</param>
public sealed class CatalogChangedEventArgs(CatalogChange Change, long Revision) : EventArgs
{
    /// <summary>Gets the change details.</summary>
    public CatalogChange Change { get; } = Change;

    /// <summary>Gets the new catalogue revision.</summary>
    public long Revision { get; } = Revision;
}

/// <summary>The runtime authority on what devices and capabilities are (spec MOD §4.2).</summary>
public interface ITypeCatalog
{
    /// <summary>Gets the catalogue revision number; increments on every change.</summary>
    long Revision { get; }

    /// <summary>Gets the flattened device type definition matching the exact reference.</summary>
    /// <param name="type">The type reference.</param>
    /// <returns>The definition, or <c>model.type.not_found</c>.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Name fixed by spec MOD §4.2.")]
    Result<DeviceTypeDefinition> Get(TypeRef type);

    /// <summary>Gets the highest semantic version of a type for the given major version.</summary>
    /// <param name="id">The type identifier.</param>
    /// <param name="major">The major version number.</param>
    /// <returns>The latest matching definition, or <c>model.type.not_found</c>.</returns>
    Result<DeviceTypeDefinition> GetLatest(TypeId id, int major);

    /// <summary>Lists device type definitions matching the query criteria.</summary>
    /// <param name="query">The query filters.</param>
    /// <returns>All matching device type definitions.</returns>
    IReadOnlyList<DeviceTypeDefinition> List(TypeQuery query);

    /// <summary>Gets the capability definition for the given id and semantic version.</summary>
    /// <param name="id">The capability identifier.</param>
    /// <param name="version">The capability version.</param>
    /// <returns>The capability definition, or <c>model.capability.not_found</c>.</returns>
    Result<CapabilityDefinition> GetCapability(CapabilityId id, SemVersion version);

    /// <summary>Registers an integration package's type documents atomically into the catalogue.</summary>
    /// <param name="package">The package identifier.</param>
    /// <param name="packageVersion">The package semantic version.</param>
    /// <param name="documents">The package documents.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The change result, or validation/conflict error.</returns>
    Task<Result<CatalogChange>> RegisterPackageAsync(
        PackageId package,
        SemVersion packageVersion,
        PackageTypeDocuments documents,
        CancellationToken ct);

    /// <summary>Unregisters a package version from the active catalogue snapshot.</summary>
    /// <param name="package">The package identifier.</param>
    /// <param name="packageVersion">The package semantic version.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The result.</returns>
    Task<Result> UnregisterPackageAsync(PackageId package, SemVersion packageVersion, CancellationToken ct);

    /// <summary>Raised after an atomic snapshot swap has completed.</summary>
    event EventHandler<CatalogChangedEventArgs>? Changed;
}
