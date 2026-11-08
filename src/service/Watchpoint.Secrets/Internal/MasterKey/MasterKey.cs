using System.Security.Cryptography;

namespace Watchpoint.Secrets.Internal;

/// <summary>A 256-bit master key (KEK) in a pinned buffer; <see cref="KeyId"/> = first 4 bytes of SHA-256 of the key, hex.</summary>
internal sealed class MasterKey : IDisposable
{
    public const int Size = 32;
    private readonly byte[] _bytes;

    public MasterKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != Size)
        {
            throw new ArgumentException($"A master key has {Size} bytes.", nameof(key));
        }

        _bytes = GC.AllocateArray<byte>(Size, pinned: true);
        key.CopyTo(_bytes);
        KeyId = Convert.ToHexStringLower(SHA256.HashData(_bytes).AsSpan(0, 4));
    }

    public string KeyId { get; }

    public ReadOnlySpan<byte> Bytes => _bytes;

    public static MasterKey Generate()
    {
        Span<byte> key = stackalloc byte[Size];
        RandomNumberGenerator.Fill(key);
        try
        {
            return new MasterKey(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_bytes);
}

/// <summary>The current master key and retired ones still able to unwrap old data keys (until rewrapped).</summary>
internal sealed class MasterKeyRing(MasterKey current, IEnumerable<MasterKey>? retired = null)
{
    private readonly Dictionary<string, MasterKey> _keys =
        (retired ?? []).Append(current).ToDictionary(k => k.KeyId, StringComparer.Ordinal);

    public MasterKey Current { get; } = current;

    public MasterKey? Find(string keyId) => _keys.GetValueOrDefault(keyId);
}
