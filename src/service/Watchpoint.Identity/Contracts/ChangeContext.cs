using Watchpoint.Kernel.Ids;

namespace Watchpoint.Identity.Contracts;

/// <summary>Context carrying actor and provenance for identity domain changes.</summary>
/// <param name="Actor">The user id performing the change, or null if system/bootstrap.</param>
/// <param name="Origin">The origin of the change (e.g. "api", "seed", "bootstrap").</param>
/// <param name="Comment">Optional comment.</param>
public sealed record ChangeContext(UserId? Actor, string Origin, string? Comment = null);
