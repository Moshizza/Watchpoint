using System.Text.Json.Nodes;
using NSec.Cryptography;

namespace Watchpoint.Integrations.Testing;

/// <summary>An Ed25519 key pair for signing test packages.</summary>
public sealed class TestKey : IDisposable
{
    private readonly Key _key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    public TestKey(string keyId) => KeyId = keyId;

    public string KeyId { get; }

    public byte[] PublicKey => _key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

    /// <summary>The content of <c>signature.sig</c> for <paramref name="manifest"/>.</summary>
    public string SignatureFile(byte[] manifest) => new JsonObject
    {
        ["alg"] = "Ed25519",
        ["keyId"] = KeyId,
        ["signature"] = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(_key, manifest)),
    }.ToJsonString();

    /// <summary>Writes <c>&lt;keyId&gt;.pub</c> into a development keys folder.</summary>
    public void WriteDevelopmentKey(string devKeysDir)
    {
        Directory.CreateDirectory(devKeysDir);
        File.WriteAllText(Path.Combine(devKeysDir, KeyId + ".pub"), Convert.ToBase64String(PublicKey));
    }

    public void Dispose() => _key.Dispose();
}
