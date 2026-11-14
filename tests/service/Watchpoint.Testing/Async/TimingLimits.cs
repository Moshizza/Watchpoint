namespace Watchpoint.Testing.Async;

/// <summary>
/// Limits for tests that assert real elapsed time (TST §4.7). The spec's strict limit applies on a developer machine
/// and in benchmark runs (<c>WATCHPOINT_STRICT_TIMING=1</c>); on shared CI runners (<c>CI=true</c>) a wider ceiling
/// applies, because their CPU and disk I/O noise alone exceeds tight limits. The ceiling still catches a broken
/// implementation (an order of magnitude slower), not a regression of a few percent: that is the benchmark's job.
/// </summary>
public static class TimingLimits
{
    /// <summary>The environment variable that forces the strict limit, even with <c>CI=true</c> (benchmark runs).</summary>
    public const string StrictVariable = "WATCHPOINT_STRICT_TIMING";

    /// <summary>Gets the limit for the current process environment.</summary>
    /// <param name="strict">The spec's limit (developer machine, benchmark run).</param>
    /// <param name="sharedCiCeiling">The ceiling on shared CI runners; at least <paramref name="strict"/>.</param>
    /// <returns><paramref name="sharedCiCeiling"/> when <c>CI=true</c> and the strict variable is not <c>1</c>; otherwise <paramref name="strict"/>.</returns>
    public static TimeSpan For(TimeSpan strict, TimeSpan sharedCiCeiling) => For(strict, sharedCiCeiling, Environment.GetEnvironmentVariable);

    /// <summary>Gets the limit for the given environment.</summary>
    /// <param name="strict">The spec's limit (developer machine, benchmark run).</param>
    /// <param name="sharedCiCeiling">The ceiling on shared CI runners; at least <paramref name="strict"/>.</param>
    /// <param name="environment">Reads an environment variable by name; <see langword="null"/> when unset.</param>
    /// <returns><paramref name="sharedCiCeiling"/> when <c>CI=true</c> and the strict variable is not <c>1</c>; otherwise <paramref name="strict"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sharedCiCeiling"/> is below <paramref name="strict"/>.</exception>
    public static TimeSpan For(TimeSpan strict, TimeSpan sharedCiCeiling, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentOutOfRangeException.ThrowIfLessThan(sharedCiCeiling, strict);
        var forceStrict = environment(StrictVariable) == "1";
        var sharedCi = string.Equals(environment("CI"), "true", StringComparison.OrdinalIgnoreCase);
        return sharedCi && !forceStrict ? sharedCiCeiling : strict;
    }
}
