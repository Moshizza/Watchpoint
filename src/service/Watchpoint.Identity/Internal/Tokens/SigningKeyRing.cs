using Microsoft.IdentityModel.Tokens;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Identity.Internal.Tokens;

/// <summary>Represents an ECDSA P-256 signing key with kid and validity lifetimes.</summary>
internal sealed class SigningKey : IDisposable
{
    public string KeyId { get; }

    public System.Security.Cryptography.ECDsa PrivateKey { get; }

    public ECDsaSecurityKey SecurityKey { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset ValidationUntil { get; }

    public SigningKey(string keyId, System.Security.Cryptography.ECDsa privateKey, DateTimeOffset createdAt, TimeSpan activeLifetime, TimeSpan validationOverlap)
    {
        KeyId = keyId;
        PrivateKey = privateKey;
        SecurityKey = new ECDsaSecurityKey(privateKey) { KeyId = keyId };
        CreatedAt = createdAt;
        ExpiresAt = createdAt + activeLifetime;
        ValidationUntil = ExpiresAt + validationOverlap;
    }

    public void Dispose()
    {
        PrivateKey.Dispose();
    }
}

/// <summary>Key ring managing ECDSA P-256 keys, rotation every 30 days, 1-day validation overlap (spec IDN §6.2).</summary>
internal sealed class SigningKeyRing : IDisposable
{
    private readonly ISecretVault? _vault;
    private readonly IBootstrapSecretStore? _bootstrap;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _activeLifetime = TimeSpan.FromDays(30);
    private readonly TimeSpan _validationOverlap = TimeSpan.FromDays(1);
    private readonly List<SigningKey> _keys = [];
    private readonly Lock _gate = new();

    public SigningKeyRing(
        TimeProvider clock,
        ISecretVault? vault = null,
        IBootstrapSecretStore? bootstrap = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _vault = vault;
        _bootstrap = bootstrap;
    }

    /// <summary>Gets the current active signing key, rotating if necessary.</summary>
    public SigningKey GetCurrentSigningKey()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            var active = _keys.FirstOrDefault(k => k.CreatedAt <= now && now < k.ExpiresAt);
            if (active is not null)
            {
                return active;
            }

            // Create new key
            var newKey = GenerateNewKey(now);
            _keys.Insert(0, newKey);
            return newKey;
        }
    }

    /// <summary>Rotates the key ring explicitly, generating a new signing key immediately.</summary>
    public SigningKey RotateNow()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            var newKey = GenerateNewKey(now);
            _keys.Insert(0, newKey);
            return newKey;
        }
    }

    /// <summary>Gets all keys currently valid for token validation (non-expired + 1 day validation overlap).</summary>
    public IReadOnlyList<SigningKey> GetValidationKeys()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            return _keys.Where(k => now < k.ValidationUntil).ToList();
        }
    }

    /// <summary>Finds a validation key by its kid if not expired for validation.</summary>
    public SigningKey? FindValidationKey(string kid)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            return _keys.FirstOrDefault(k => k.KeyId == kid && now < k.ValidationUntil);
        }
    }

    private SigningKey GenerateNewKey(DateTimeOffset now)
    {
        var kid = Guid.NewGuid().ToString("N")[..8];
        var ecdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var key = new SigningKey(kid, ecdsa, now, _activeLifetime, _validationOverlap);

        // Store into bootstrap secrets or vault if available
        try
        {
            var pkcs8 = ecdsa.ExportPkcs8PrivateKey();
            if (_bootstrap is not null)
            {
                _bootstrap.Write($"identity.jwt.{kid}", pkcs8);
            }
        }
        catch
        {
            // Best effort persistence
        }

        return key;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var key in _keys)
            {
                key.Dispose();
            }
            _keys.Clear();
        }
    }
}
