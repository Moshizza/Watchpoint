using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Cbor;
using Fido2NetLib.Objects;

namespace Watchpoint.Identity.Tests;

/// <summary>
/// A WebAuthn authenticator in software (no hardware): one P-256 key per instance, attestation format <c>none</c>, a
/// signature counter the test controls. It produces the JSON a browser would hand to the server.
/// </summary>
internal sealed class SoftwareAuthenticator : IDisposable
{
    private static readonly string[] _transports = ["internal"];
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

    public uint Counter { get; set; }

    /// <summary>Answers a registration ceremony.</summary>
    public string Register(string optionsJson, string origin, string? rpIdOverride = null, byte[]? challengeOverride = null)
    {
        var options = CredentialCreateOptions.FromJson(optionsJson);
        var clientData = ClientData("webauthn.create", challengeOverride ?? options.Challenge, origin);
        var attested = new AttestedCredentialData(Guid.Empty, CredentialId, new CredentialPublicKey(_key, COSE.Algorithm.ES256));
        var authData = new AuthenticatorData(SHA256.HashData(Encoding.UTF8.GetBytes(rpIdOverride ?? options.Rp.Id)),
            AuthenticatorFlags.UP | AuthenticatorFlags.UV | AuthenticatorFlags.AT, Counter, attested, null).ToByteArray();
        var attestation = new CborMap { { "fmt", "none" }, { "attStmt", new CborMap() }, { "authData", authData } }.Encode();

        return JsonSerializer.Serialize(new
        {
            id = B64(CredentialId),
            rawId = B64(CredentialId),
            type = "public-key",
            response = new { attestationObject = B64(attestation), clientDataJSON = B64(clientData), transports = _transports },
            clientExtensionResults = new { },
        });
    }

    /// <summary>Answers an assertion ceremony; <paramref name="counter"/> defaults to the next value.</summary>
    public string Assert(string optionsJson, string origin, byte[] userHandle, uint? counter = null, string? rpIdOverride = null, byte[]? challengeOverride = null)
    {
        var options = AssertionOptions.FromJson(optionsJson);
        Counter = counter ?? Counter + 1;
        var clientData = ClientData("webauthn.get", challengeOverride ?? options.Challenge, origin);
        var authData = new AuthenticatorData(SHA256.HashData(Encoding.UTF8.GetBytes(rpIdOverride ?? options.RpId!)),
            AuthenticatorFlags.UP | AuthenticatorFlags.UV, Counter, null, null).ToByteArray();
        var signature = _key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return JsonSerializer.Serialize(new
        {
            id = B64(CredentialId),
            rawId = B64(CredentialId),
            type = "public-key",
            response = new { authenticatorData = B64(authData), clientDataJSON = B64(clientData), signature = B64(signature), userHandle = B64(userHandle) },
            clientExtensionResults = new { },
        });
    }

    private static byte[] ClientData(string type, byte[] challenge, string origin) =>
        JsonSerializer.SerializeToUtf8Bytes(new { type, challenge = B64(challenge), origin, crossOrigin = false });

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _key.Dispose();
}
