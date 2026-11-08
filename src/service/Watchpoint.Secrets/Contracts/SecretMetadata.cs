using Watchpoint.Kernel.Ids;

namespace Watchpoint.Secrets.Contracts;

/// <summary>What is known about a secret without decrypting it.</summary>
/// <param name="Id">The id.</param>
/// <param name="Name">Name for humans.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Owner">The owner.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="UpdatedAt">Last replacement.</param>
/// <param name="LastAccessedAt">Last read (updated at most once per minute).</param>
/// <param name="ExpiresAt">Expiry, if any.</param>
/// <param name="Version">Version (1 on creation, +1 on each replacement).</param>
public sealed record SecretMetadata(SecretId Id, string Name, SecretKind Kind, SecretOwner Owner,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastAccessedAt,
    DateTimeOffset? ExpiresAt, int Version);
