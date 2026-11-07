namespace Watchpoint.Testing.Containers;

/// <summary>Options of the container fixtures; the defaults suit normal test runs.</summary>
public sealed record ContainerFixtureOptions
{
    /// <summary>Gets the Docker availability probe; <see cref="DockerAvailability.IsAvailable"/> when <see langword="null"/>.</summary>
    public Func<bool>? DockerProbe { get; init; }

    /// <summary>
    /// Gets a value indicating whether missing Docker fails the test instead of skipping it;
    /// <see cref="DockerAvailability.ContainersRequired"/> (<c>WATCHPOINT_REQUIRE_CONTAINERS=1</c>) when <see langword="null"/>.
    /// </summary>
    public bool? RequireContainers { get; init; }
}
