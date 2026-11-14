namespace Watchpoint.Hosting.Supervision;

/// <summary>Resource limits of a supervised process (spec HST §7.1; enforced from T4.01 for IHO and SCR).</summary>
/// <param name="MemoryBytes">The memory limit in bytes, or <see langword="null"/> for none.</param>
/// <param name="CpuCores">The CPU limit in cores, or <see langword="null"/> for none.</param>
public sealed record ResourceLimits(long? MemoryBytes = null, double? CpuCores = null);
