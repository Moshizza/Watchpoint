using NSec.Cryptography;
using Watchpoint.Audit.Internal;

namespace Watchpoint.Audit.Tests;

/// <summary>An Ed25519 signer with an in-memory key.</summary>
internal sealed class TestSigner : IAuditSigner, IDisposable
{
    private readonly Key _key = new(SignatureAlgorithm.Ed25519);

    public ValueTask<(string KeyId, byte[] Signature)> SignAsync(byte[] data, CancellationToken ct) =>
        ValueTask.FromResult(("test-key", SignatureAlgorithm.Ed25519.Sign(_key, data)));

    public ValueTask<bool> VerifyAsync(string keyId, byte[] data, byte[] signature, CancellationToken ct) =>
        ValueTask.FromResult(keyId == "test-key" && SignatureAlgorithm.Ed25519.Verify(_key.PublicKey, data, signature));

    public void Dispose() => _key.Dispose();
}
