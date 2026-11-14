using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Identity.Internal.Tokens;

/// <summary>Represents an ECDSA P-256 signing key with kid and validity lifetimes.</summary>
internal sealed class SigningKey : IDisposable
{
    private readonly TimeSpan _validationOverlap;

    public string KeyId { get; }

    public ECDsa PrivateKey { get; }

    public ECDsaSecurityKey SecurityKey { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets the instant from which the key no longer signs.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Gets the instant from which the key no longer validates.</summary>
    public DateTimeOffset ValidationUntil => ExpiresAt + _validationOverlap;

    public SigningKey(string keyId, ECDsa privateKey, DateTimeOffset createdAt, DateTimeOffset expiresAt, TimeSpan validationOverlap)
    {
        KeyId = keyId;
        PrivateKey = privateKey;
        SecurityKey = new ECDsaSecurityKey(privateKey) { KeyId = keyId };
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        _validationOverlap = validationOverlap;
    }

    /// <summary>Stops signing with this key now; it validates for the overlap only.</summary>
    public void Retire(DateTimeOffset now)
    {
        if (now < ExpiresAt)
        {
            ExpiresAt = now;
        }
    }

    public void Dispose()
    {
        PrivateKey.Dispose();
    }
}

/// <summary>
/// Key ring managing ECDSA P-256 keys, rotation every 30 days, 1-day validation overlap (spec IDN §6.2).
/// With a bootstrap secret store, every key is stored as <c>identity.jwt.&lt;kid&gt;</c> (PKCS#8) and listed in
/// <c>identity.jwt.index</c>, and the ring is loaded from there: tokens stay valid over a restart and every node
/// validates with the same keys. A key that cannot be stored is never used. Without a store (unit tests) keys
/// live in memory only.
/// </summary>
internal sealed class SigningKeyRing : IDisposable
{
    internal const string IndexSecretName = "identity.jwt.index";
    private static readonly TimeSpan _reloadInterval = TimeSpan.FromSeconds(30);

    private readonly IBootstrapSecretStore? _bootstrap;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _activeLifetime = TimeSpan.FromDays(30);
    private readonly TimeSpan _validationOverlap = TimeSpan.FromDays(1);
    private readonly List<SigningKey> _keys = [];
    private readonly Lock _gate = new();
    private bool _loaded;
    private DateTimeOffset _lastReload = DateTimeOffset.MinValue;

    public SigningKeyRing(
        TimeProvider clock,
        ISecretVault? vault = null,
        IBootstrapSecretStore? bootstrap = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ = vault;
        _bootstrap = bootstrap;
    }

    /// <summary>Gets the current active signing key, rotating if necessary.</summary>
    public SigningKey GetCurrentSigningKey()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            EnsureLoaded(now);
            var active = FindActive(now);
            if (active is not null)
            {
                return active;
            }

            // Another node may have rotated already: look before creating a second key.
            Reload(now);
            return FindActive(now) ?? AddNewKey(now);
        }
    }

    /// <summary>Rotates the key ring explicitly: a new key signs at once, the previous ones validate for one more day.</summary>
    public SigningKey RotateNow()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            EnsureLoaded(now);
            foreach (var key in _keys)
            {
                key.Retire(now);
            }

            return AddNewKey(now);
        }
    }

    /// <summary>Gets all keys currently valid for token validation (non-expired + 1 day validation overlap).</summary>
    public IReadOnlyList<SigningKey> GetValidationKeys()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            EnsureLoaded(now);
            return _keys.Where(k => now < k.ValidationUntil).ToList();
        }
    }

    /// <summary>
    /// Finds a validation key by its kid if not expired for validation. An unknown kid triggers a reload from the
    /// store (a key created by another node), at most once per 30 s.
    /// </summary>
    public SigningKey? FindValidationKey(string? kid)
    {
        if (string.IsNullOrEmpty(kid))
        {
            return null;
        }

        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            EnsureLoaded(now);
            var key = _keys.FirstOrDefault(k => k.KeyId == kid);
            if (key is null && _bootstrap is not null && now - _lastReload >= _reloadInterval)
            {
                Reload(now);
                key = _keys.FirstOrDefault(k => k.KeyId == kid);
            }

            return key is not null && now < key.ValidationUntil ? key : null;
        }
    }

    private SigningKey? FindActive(DateTimeOffset now) =>
        _keys.FirstOrDefault(k => k.CreatedAt <= now && now < k.ExpiresAt);

    private void EnsureLoaded(DateTimeOffset now)
    {
        if (!_loaded)
        {
            Reload(now);
            _loaded = true;
        }
    }

    private void Reload(DateTimeOffset now)
    {
        _lastReload = now;
        if (_bootstrap is null)
        {
            return;
        }

        foreach (var entry in ReadIndex())
        {
            var existing = _keys.FirstOrDefault(k => k.KeyId == entry.Kid);
            if (existing is not null)
            {
                // Another node may have retired the key.
                existing.Retire(entry.ExpiresAt);
                continue;
            }

            if (now >= entry.ExpiresAt + _validationOverlap)
            {
                continue;
            }

            var read = _bootstrap.Read(SecretName(entry.Kid));
            if (!read.TryGetValue(out var secret, out var error))
            {
                throw new InvalidOperationException($"Token signing key {entry.Kid} cannot be read from the bootstrap secrets: {error.Code.Value}.");
            }

            using (secret)
            {
                var ecdsa = secret.Use(static bytes =>
                {
                    var key = ECDsa.Create();
                    try
                    {
                        key.ImportPkcs8PrivateKey(bytes, out _);
                        return key;
                    }
                    catch
                    {
                        key.Dispose();
                        throw;
                    }
                });

                if (ecdsa.KeySize != 256)
                {
                    ecdsa.Dispose();
                    throw new InvalidOperationException($"Token signing key {entry.Kid} is not a P-256 key.");
                }

                _keys.Add(new SigningKey(entry.Kid, ecdsa, entry.CreatedAt, entry.ExpiresAt, _validationOverlap));
            }
        }

        _keys.Sort(static (a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
    }

    private SigningKey AddNewKey(DateTimeOffset now)
    {
        // Keys that validate nothing any more leave the ring.
        foreach (var dead in _keys.Where(k => now >= k.ValidationUntil).ToList())
        {
            _keys.Remove(dead);
            dead.Dispose();
        }

        var kid = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = new SigningKey(kid, ecdsa, now, now + _activeLifetime, _validationOverlap);

        if (_bootstrap is not null)
        {
            try
            {
                Persist(key);
            }
            catch
            {
                key.Dispose();
                throw;
            }
        }

        _keys.Insert(0, key);
        return key;
    }

    private void Persist(SigningKey key)
    {
        var pkcs8 = key.PrivateKey.ExportPkcs8PrivateKey();
        try
        {
            var written = _bootstrap!.Write(SecretName(key.KeyId), pkcs8);
            if (!written.IsSuccess)
            {
                throw new InvalidOperationException($"Token signing key cannot be stored in the bootstrap secrets: {written.Error.Code.Value}.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        var entries = _keys.Select(k => new IndexEntry(k.KeyId, k.CreatedAt, k.ExpiresAt))
            .Prepend(new IndexEntry(key.KeyId, key.CreatedAt, key.ExpiresAt))
            .ToList();
        var index = _bootstrap.Write(IndexSecretName, JsonSerializer.SerializeToUtf8Bytes(entries));
        if (!index.IsSuccess)
        {
            throw new InvalidOperationException($"Token signing key index cannot be stored in the bootstrap secrets: {index.Error.Code.Value}.");
        }
    }

    private List<IndexEntry> ReadIndex()
    {
        if (!_bootstrap!.Exists(IndexSecretName))
        {
            return [];
        }

        var read = _bootstrap.Read(IndexSecretName);
        if (!read.TryGetValue(out var secret, out var error))
        {
            // Fail closed: silently starting a new ring would invalidate every token and hide tampering.
            throw new InvalidOperationException($"Token signing key index cannot be read from the bootstrap secrets: {error.Code.Value}.");
        }

        using (secret)
        {
            return secret.Use(static bytes => JsonSerializer.Deserialize<List<IndexEntry>>(bytes)) ?? [];
        }
    }

    private static string SecretName(string kid) => $"identity.jwt.{kid}";

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

    private sealed record IndexEntry(string Kid, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
}
