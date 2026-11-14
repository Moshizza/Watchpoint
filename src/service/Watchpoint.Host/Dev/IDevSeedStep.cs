using Watchpoint.Kernel.Results;

namespace Watchpoint.Host.Dev;

/// <summary>
/// Applies one section of the development seed through a module's public contract (spec HST §9). The host
/// registers one step per section whose module it composes; <see cref="DevSeedStartupTask"/> runs them in the
/// order of <see cref="DevSeedDocument.Sections"/>.
/// </summary>
public interface IDevSeedStep
{
    /// <summary>Gets the section this step applies (a name from <see cref="DevSeedDocument.Sections"/>).</summary>
    string Section { get; }

    /// <summary>Returns whether the module already holds data of this section: the seed is then not applied at all.</summary>
    /// <param name="seed">The validated seed.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><see langword="true"/> when the store is not empty.</returns>
    Task<bool> HasDataAsync(DevSeedDocument seed, CancellationToken ct);

    /// <summary>Creates the objects of this section.</summary>
    /// <param name="seed">The validated seed.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Success, or the module's error.</returns>
    Task<Result> ApplyAsync(DevSeedDocument seed, CancellationToken ct);
}
