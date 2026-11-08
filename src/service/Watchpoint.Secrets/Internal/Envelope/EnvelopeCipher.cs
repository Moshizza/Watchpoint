using System.Security.Cryptography;
using System.Text;
using Watchpoint.Kernel.Results;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Secrets.Internal;

/// <summary>An encrypted secret version (spec SEC §6.1, §8).</summary>
internal sealed record EncryptedValue(string KeyId, byte[] WrappedDek, byte[] DekNonce, byte[] Ciphertext, byte[] Nonce);

/// <summary>Envelope encryption: AES-256-GCM with a random data key per version, wrapped by the master key.</summary>
internal static class EnvelopeCipher
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    public static EncryptedValue Encrypt(Guid id, int version, ReadOnlySpan<byte> plaintext, MasterKey masterKey)
    {
        var dek = GC.AllocateArray<byte>(KeySize, pinned: true);
        try
        {
            RandomNumberGenerator.Fill(dek);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[plaintext.Length + TagSize];
            using (var aes = new AesGcm(dek, TagSize))
            {
                aes.Encrypt(nonce, plaintext, ciphertext.AsSpan(0, plaintext.Length), ciphertext.AsSpan(plaintext.Length), ValueAad(id, version));
            }

            var (wrapped, dekNonce) = WrapDek(id, version, dek, masterKey);
            return new EncryptedValue(masterKey.KeyId, wrapped, dekNonce, ciphertext, nonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public static Result<SecretValue> Decrypt(Guid id, int version, EncryptedValue value, MasterKeyRing keys)
    {
        var dek = GC.AllocateArray<byte>(KeySize, pinned: true);
        try
        {
            if (!TryUnwrapDek(id, version, value, keys, dek))
            {
                return SecretsErrors.IntegrityFailed(id.ToString("D"));
            }

            var length = value.Ciphertext.Length - TagSize;
            if (length < 0 || value.Nonce.Length != NonceSize)
            {
                return SecretsErrors.IntegrityFailed(id.ToString("D"));
            }

            var plaintext = SecretValue.AllocatePinned(length);
            try
            {
                using var aes = new AesGcm(dek, TagSize);
                aes.Decrypt(value.Nonce, value.Ciphertext.AsSpan(0, length), value.Ciphertext.AsSpan(length), plaintext, ValueAad(id, version));
                return SecretValue.Own(plaintext);
            }
            catch (AuthenticationTagMismatchException)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                return SecretsErrors.IntegrityFailed(id.ToString("D"));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>Re-wraps the data key with the current master key (value ciphertext unchanged).</summary>
    public static EncryptedValue? Rewrap(Guid id, int version, EncryptedValue value, MasterKeyRing keys)
    {
        var dek = GC.AllocateArray<byte>(KeySize, pinned: true);
        try
        {
            if (!TryUnwrapDek(id, version, value, keys, dek))
            {
                return null;
            }

            var (wrapped, dekNonce) = WrapDek(id, version, dek, keys.Current);
            return value with { KeyId = keys.Current.KeyId, WrappedDek = wrapped, DekNonce = dekNonce };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>Encrypts directly with the master key (bootstrap secrets): ciphertext||tag.</summary>
    public static (byte[] Nonce, byte[] Ciphertext) SealWithMasterKey(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad, MasterKey key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length + TagSize];
        using var aes = new AesGcm(key.Bytes, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext.AsSpan(0, plaintext.Length), ciphertext.AsSpan(plaintext.Length), aad);
        return (nonce, ciphertext);
    }

    public static SecretValue? OpenWithMasterKey(byte[] nonce, byte[] ciphertext, ReadOnlySpan<byte> aad, MasterKey key)
    {
        var length = ciphertext.Length - TagSize;
        if (length < 0 || nonce.Length != NonceSize)
        {
            return null;
        }

        var plaintext = SecretValue.AllocatePinned(length);
        try
        {
            using var aes = new AesGcm(key.Bytes, TagSize);
            aes.Decrypt(nonce, ciphertext.AsSpan(0, length), ciphertext.AsSpan(length), plaintext, aad);
            return SecretValue.Own(plaintext);
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return null;
        }
    }

    private static (byte[] Wrapped, byte[] Nonce) WrapDek(Guid id, int version, ReadOnlySpan<byte> dek, MasterKey key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var wrapped = new byte[KeySize + TagSize];
        using var aes = new AesGcm(key.Bytes, TagSize);
        aes.Encrypt(nonce, dek, wrapped.AsSpan(0, KeySize), wrapped.AsSpan(KeySize), DekAad(id, version, key.KeyId));
        return (wrapped, nonce);
    }

    private static bool TryUnwrapDek(Guid id, int version, EncryptedValue value, MasterKeyRing keys, Span<byte> dek)
    {
        var key = keys.Find(value.KeyId);
        if (key is null || value.WrappedDek.Length != KeySize + TagSize || value.DekNonce.Length != NonceSize)
        {
            return false;
        }

        try
        {
            using var aes = new AesGcm(key.Bytes, TagSize);
            aes.Decrypt(value.DekNonce, value.WrappedDek.AsSpan(0, KeySize), value.WrappedDek.AsSpan(KeySize), dek, DekAad(id, version, key.KeyId));
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }
    }

    private static byte[] ValueAad(Guid id, int version) => Encoding.UTF8.GetBytes($"wp-secret|{id:D}|{version}");

    private static byte[] DekAad(Guid id, int version, string keyId) => Encoding.UTF8.GetBytes($"wp-dek|{id:D}|{version}|{keyId}");
}
