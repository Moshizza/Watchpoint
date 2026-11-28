namespace Watchpoint.Audit.Internal;

/// <summary>Signs and verifies checkpoints with the site audit signing key (SEC key purpose <c>audit</c>).</summary>
internal interface IAuditSigner
{
    ValueTask<(string KeyId, byte[] Signature)> SignAsync(byte[] data, CancellationToken ct);

    ValueTask<bool> VerifyAsync(string keyId, byte[] data, byte[] signature, CancellationToken ct);
}
