using System.Text.Json;
using NSec.Cryptography;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Integrations.Internal.Packages;

/// <summary>
/// Verifies <c>signature.sig</c> (spec IRT §4.4): an Ed25519 signature of the exact bytes of <c>manifest.json</c> by a
/// trusted key. The manifest lists the SHA-256 of every file, so the signature covers the whole package.
/// </summary>
internal sealed class SignatureVerifier(TrustedKeys keys)
{
    public const string SignatureFile = "signature.sig";
    private const int SignatureLength = 64;
    private const int MaxSignatureFileBytes = 4096;

    /// <summary>Verifies the package in <paramref name="packageDir"/>.</summary>
    /// <returns>Whether the key is a development key; or <c>irt.package.unsigned</c> / <c>signature_invalid</c> / <c>untrusted_key</c>.</returns>
    public Result<bool> Verify(string packageDir, ReadOnlySpan<byte> manifestBytes)
    {
        var file = Path.Combine(packageDir, SignatureFile);
        if (!File.Exists(file))
        {
            return Error.Create(ErrorKind.Validation, IntegrationErrors.PackageUnsigned);
        }

        if (new FileInfo(file).Length > MaxSignatureFileBytes || !TryRead(File.ReadAllBytes(file), out var keyId, out var signature))
        {
            return Invalid("malformed");
        }

        if (keys.Find(keyId) is not { } key)
        {
            return Error.Create(ErrorKind.Validation, IntegrationErrors.PackageUntrustedKey, ("keyId", keyId));
        }

        var algorithm = SignatureAlgorithm.Ed25519;
        if (!PublicKey.TryImport(algorithm, key.PublicKey, KeyBlobFormat.RawPublicKey, out var publicKey) || publicKey is null)
        {
            return Error.Create(ErrorKind.Validation, IntegrationErrors.PackageUntrustedKey, ("keyId", keyId));
        }

        return algorithm.Verify(publicKey, manifestBytes, signature) ? key.Development : Invalid("mismatch");
    }

    private static Error Invalid(string reason) =>
        Error.Create(ErrorKind.Validation, IntegrationErrors.PackageSignatureInvalid, ("reason", reason));

    private static bool TryRead(byte[] json, out string keyId, out byte[] signature)
    {
        keyId = string.Empty;
        signature = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("alg", out var alg) || alg.ValueKind != JsonValueKind.String || alg.GetString() != "Ed25519"
                || !root.TryGetProperty("keyId", out var id) || id.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("signature", out var sig) || sig.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            keyId = id.GetString()!;
            var bytes = new byte[SignatureLength];
            if (!Convert.TryFromBase64String(sig.GetString()!, bytes, out var written) || written != SignatureLength)
            {
                return false;
            }

            signature = bytes;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
