using DotNet.Testcontainers.Configurations;
using Xunit;

namespace Watchpoint.Testing.Containers;

/// <summary>
/// Docker availability rules (spec TST §5.4): without Docker, container tests are skipped locally; CI sets
/// <c>WATCHPOINT_REQUIRE_CONTAINERS=1</c>, which turns the skip into a failure.
/// </summary>
public static class DockerAvailability
{
    /// <summary>The environment variable that makes containers mandatory.</summary>
    public const string RequireVariable = "WATCHPOINT_REQUIRE_CONTAINERS";

    /// <summary>The skip reason used when Docker is missing.</summary>
    public const string SkipReason = "Docker not available";

    /// <summary>Gets a value indicating whether <c>WATCHPOINT_REQUIRE_CONTAINERS</c> is <c>1</c> or <c>true</c>.</summary>
    public static bool ContainersRequired =>
        Environment.GetEnvironmentVariable(RequireVariable) is "1" or "true" or "TRUE" or "True";

    /// <summary>Returns whether a Docker endpoint was found by Testcontainers.</summary>
    /// <returns><see langword="true"/> when Docker is reachable.</returns>
    public static bool IsAvailable()
    {
        try
        {
            return TestcontainersSettings.OS.DockerEndpointAuthConfig is not null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Returns when Docker is available; otherwise skips the current test, or fails it when containers are required.</summary>
    /// <param name="options">The fixture options.</param>
    /// <exception cref="InvalidOperationException">Docker is missing and containers are required.</exception>
    public static void Ensure(ContainerFixtureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if ((options.DockerProbe ?? IsAvailable)())
        {
            return;
        }

        if (options.RequireContainers ?? ContainersRequired)
        {
            throw new InvalidOperationException($"{SkipReason}, but {RequireVariable}=1 requires container tests to run.");
        }

        Assert.Skip(SkipReason);
    }
}
