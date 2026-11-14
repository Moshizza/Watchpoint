using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Documents;
using Watchpoint.Model.Documents.Library;

namespace Watchpoint.Integrations.Contracts;

/// <summary>A package version known to the catalogue (spec IRT §4.2).</summary>
/// <param name="Id">The package id (from the manifest; derived from the file name while the manifest is unreadable).</param>
/// <param name="Version">The package version (<c>0.0.0</c> while the manifest is unreadable).</param>
/// <param name="State">The state.</param>
/// <param name="Vendor">The vendor name.</param>
/// <param name="Category">The category.</param>
/// <param name="Source"><c>builtin</c> or <c>user</c>.</param>
/// <param name="FilePath">The <c>.wpkg</c> file or folder the package came from.</param>
/// <param name="Connectors">The connectors.</param>
/// <param name="Licence">The licence requirement.</param>
/// <param name="Platforms">The supported runtime identifiers.</param>
/// <param name="Isolation">The isolation mode (<c>inProcessAllowed</c>, <c>isolatedRecommended</c>, <c>isolatedRequired</c>).</param>
/// <param name="Problems">Errors (state <see cref="PackageState.Invalid"/>) and warnings.</param>
/// <param name="SignedByDevelopmentKey">Whether the signature was made with a development key.</param>
/// <param name="DiscoveredAt">When the package was first seen.</param>
public sealed record PackageInfo(PackageId Id, SemVersion Version, PackageState State,
    string Vendor, string Category, string Source, string? FilePath,
    IReadOnlyList<ConnectorInfo> Connectors, LicenceRequirement Licence,
    IReadOnlyList<string> Platforms, string Isolation, IReadOnlyList<DocumentProblem> Problems,
    bool SignedByDevelopmentKey, DateTimeOffset DiscoveredAt)
{
    /// <summary>Source of packages shipped with the product.</summary>
    public const string BuiltinSource = "builtin";

    /// <summary>Source of packages installed by users.</summary>
    public const string UserSource = "user";
}

/// <summary>A connector of a package.</summary>
/// <param name="ServerType">The server type it implements.</param>
/// <param name="Features">The features declared in the manifest.</param>
/// <param name="SupportedVendorVersions">The vendor versions supported.</param>
/// <param name="HasLocator">Whether the connector class implements <c>IServerLocator</c>.</param>
public sealed record ConnectorInfo(string ServerType, IReadOnlyList<string> Features,
    string SupportedVendorVersions, bool HasLocator);

/// <summary>The licence a package needs.</summary>
/// <param name="Class">The licence class (<c>included</c>, <c>standard</c>, <c>advanced</c>).</param>
/// <param name="FeatureKey">The feature key, when the class needs one.</param>
public sealed record LicenceRequirement(string Class, string? FeatureKey);

/// <summary>Arguments of <see cref="IPackageCatalog.Changed"/>.</summary>
/// <param name="package">The package after the change.</param>
/// <param name="previousState">The state before the change; <see langword="null"/> for a new package.</param>
public sealed class PackageChangedEventArgs(PackageInfo package, PackageState? previousState) : EventArgs
{
    /// <summary>Gets the package after the change.</summary>
    public PackageInfo Package { get; } = package;

    /// <summary>Gets the state before the change; <see langword="null"/> for a new package.</summary>
    public PackageState? PreviousState { get; } = previousState;
}
