using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Model.Documents;

namespace Watchpoint.Integrations.Contracts;

/// <summary>The integration packages of the node (spec IRT §4.2). Thread-safe; readers see a consistent snapshot.</summary>
public interface IPackageCatalog
{
    /// <summary>Lists every known package version, invalid ones included.</summary>
    /// <returns>The packages, ordered by id then version.</returns>
    IReadOnlyList<PackageInfo> List();

    /// <summary>Gets a package version.</summary>
    /// <param name="id">The package id.</param>
    /// <param name="version">The version.</param>
    /// <returns>The package, or <c>irt.package.not_found</c>.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Name fixed by spec IRT §4.2.")]
    Result<PackageInfo> Get(PackageId id, SemVersion version);

    /// <summary>Gets the highest active version of a package.</summary>
    /// <param name="id">The package id.</param>
    /// <returns>The package, or <c>irt.package.not_found</c>.</returns>
    Result<PackageInfo> GetLatestActive(PackageId id);

    /// <summary>Writes a <c>.wpkg</c> into the user folder and verifies it.</summary>
    /// <param name="wpkg">The archive.</param>
    /// <param name="fileName">The file name (<c>name.wpkg</c>, no folder).</param>
    /// <param name="actor">The user installing the package.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>
    /// The verification result (state <see cref="PackageState.Invalid"/> with problems when a check failed), or
    /// <c>irt.package.file_name_invalid</c> / <c>irt.package.unsafe_archive</c> when the upload itself is refused.
    /// </returns>
    Task<Result<PackageInfo>> InstallAsync(Stream wpkg, string fileName, UserId actor, CancellationToken ct);

    /// <summary>Enables or disables a package version; disabling stops its connections.</summary>
    /// <param name="id">The package id.</param>
    /// <param name="version">The version.</param>
    /// <param name="enabled">The new state.</param>
    /// <param name="actor">The user.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Success, <c>irt.package.not_found</c> or <c>irt.package.not_active</c> (invalid package).</returns>
    Task<Result> SetEnabledAsync(PackageId id, SemVersion version, bool enabled, UserId actor, CancellationToken ct);

    /// <summary>Gets the settings schema of a connector.</summary>
    /// <param name="id">The package id.</param>
    /// <param name="version">The version.</param>
    /// <param name="serverType">The connector's server type.</param>
    /// <returns>The schema, or <c>irt.package.not_found</c>.</returns>
    Result<ConnectionSettingsSchema> GetSettingsSchema(PackageId id, SemVersion version, string serverType);

    /// <summary>Raised after a package was added, changed state or was removed.</summary>
    event EventHandler<PackageChangedEventArgs>? Changed;
}
