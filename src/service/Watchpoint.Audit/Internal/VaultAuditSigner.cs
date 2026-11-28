using NSec.Cryptography;
using Watchpoint.Kernel.Ids;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Audit.Internal;

/// <summary>Ed25519 signer whose private key lives in the secret vault (owner module <c>audit</c>); the key is created on first use.</summary>
internal sealed class VaultAuditSigner(ISecretVault vault) : IAuditSigner, IDisposable
{
    private const string KeyName = "audit-signing-key";

    private static readonly SecretOwner _owner = new("audit", null);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Key? _key;
    private string? _keyId;

    public async ValueTask<(string KeyId, byte[] Signature)> SignAsync(byte[] data, CancellationToken ct)
    {
        await EnsureKeyAsync(ct).ConfigureAwait(false);
        return (_keyId!, SignatureAlgorithm.Ed25519.Sign(_key!, data));
    }

    public async ValueTask<bool> VerifyAsync(string keyId, byte[] data, byte[] signature, CancellationToken ct)
    {
        await EnsureKeyAsync(ct).ConfigureAwait(false);
        return string.Equals(keyId, _keyId, StringComparison.Ordinal) && SignatureAlgorithm.Ed25519.Verify(_key!.PublicKey, data, signature);
    }

    public void Dispose()
    {
        _gate.Dispose();
        _key?.Dispose();
    }

    private async Task EnsureKeyAsync(CancellationToken ct)
    {
        if (_key is not null)
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_key is not null)
            {
                return;
            }

            var existing = (await vault.ListAsync(_owner, ct).ConfigureAwait(false)).FirstOrDefault(m => m.Name == KeyName);
            SecretId id;
            if (existing is null)
            {
                using var fresh = new Key(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
                var created = await vault.CreateAsync(KeyName, SecretKind.PrivateKey, _owner, fresh.Export(KeyBlobFormat.RawPrivateKey), null, ct).ConfigureAwait(false);
                if (!created.IsSuccess)
                {
                    throw new InvalidOperationException("The audit signing key could not be created: " + created.Error.Code);
                }

                id = created.Value;
            }
            else
            {
                id = existing.Id;
            }

            var read = await vault.ReadAsync(id, _owner, "audit-checkpoint", ct).ConfigureAwait(false);
            if (!read.IsSuccess)
            {
                throw new InvalidOperationException("The audit signing key could not be read: " + read.Error.Code);
            }

            using var secret = read.Value;
            _key = secret.Use(bytes => Key.Import(SignatureAlgorithm.Ed25519, bytes, KeyBlobFormat.RawPrivateKey));
            _keyId = id.Value.ToString("D");
        }
        finally
        {
            _gate.Release();
        }
    }
}
