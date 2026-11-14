using Microsoft.Extensions.Logging;
using Watchpoint.Integrations.Contracts;

namespace Watchpoint.Integrations.Internal.Packages;

/// <summary>A public key packages may be signed with (spec IRT §4.4).</summary>
/// <param name="KeyId">The key id named by <c>signature.sig</c>.</param>
/// <param name="PublicKey">The raw Ed25519 public key (32 bytes).</param>
/// <param name="Development">Whether it is a development key.</param>
/// <param name="NotBefore">Start of validity (release keys).</param>
/// <param name="NotAfter">End of validity (release keys).</param>
internal sealed record TrustedKey(string KeyId, byte[] PublicKey, bool Development, DateTimeOffset? NotBefore = null, DateTimeOffset? NotAfter = null);

/// <summary>
/// The keys the node trusts: the release keys embedded in the product and, only when the node runs in Development,
/// the keys in <c>dev-keys/*.pub</c> (file name = key id, content = base64 of the 32-byte public key).
/// </summary>
internal sealed class TrustedKeys(IntegrationsOptions options, TimeProvider time, ILogger<TrustedKeys> logger, IReadOnlyList<TrustedKey>? releaseKeys = null)
{
    public const int PublicKeyLength = 32;

    /// <summary>
    /// The release keys embedded in the product. None exists yet: production signature checks are complete (a package
    /// signed with a key listed here verifies), but until the release process (wppack, key ceremony) adds the first key
    /// every package is untrusted outside Development. Tests inject their own list.
    /// </summary>
    public static IReadOnlyList<TrustedKey> EmbeddedReleaseKeys { get; } = [];

    private readonly IReadOnlyList<TrustedKey> _releaseKeys = releaseKeys ?? EmbeddedReleaseKeys;

    /// <summary>Finds a key that is trusted now; <see langword="null"/> when unknown, not valid today, or a development key outside Development.</summary>
    public TrustedKey? Find(string keyId)
    {
        var now = time.GetUtcNow();
        var release = _releaseKeys.FirstOrDefault(k => string.Equals(k.KeyId, keyId, StringComparison.Ordinal));
        if (release is not null)
        {
            return (release.NotBefore is null || now >= release.NotBefore) && (release.NotAfter is null || now <= release.NotAfter) ? release : null;
        }

        return options.IsDevelopment ? FindDevelopmentKey(keyId) : null;
    }

    private TrustedKey? FindDevelopmentKey(string keyId)
    {
        // The key id becomes a file name: only plain names, so that it cannot leave the folder.
        if (keyId.Length is 0 or > 64 || keyId.AsSpan().ContainsAnyExcept(_keyIdChars) || !Directory.Exists(options.DevKeysPath))
        {
            return null;
        }

        var file = Path.Combine(options.DevKeysPath, keyId + ".pub");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(file);
            if (info.Length > 1024)
            {
                IntegrationLog.DevelopmentKeyIgnored(logger, file, "too large");
                return null;
            }

            var bytes = new byte[PublicKeyLength];
            if (!Convert.TryFromBase64String(File.ReadAllText(file).Trim(), bytes, out var written) || written != PublicKeyLength)
            {
                IntegrationLog.DevelopmentKeyIgnored(logger, file, "not the base64 of a 32-byte Ed25519 public key");
                return null;
            }

            return new TrustedKey(keyId, bytes, Development: true);
        }
        catch (IOException ex)
        {
            IntegrationLog.DevelopmentKeyIgnored(logger, file, ex.Message);
            return null;
        }
    }

    private static readonly System.Buffers.SearchValues<char> _keyIdChars =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.");
}
