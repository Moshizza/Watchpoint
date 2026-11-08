using System.Security.Cryptography;
using System.Text;

namespace Watchpoint.Secrets.Contracts;

/// <summary>
/// A decrypted secret value in a pinned buffer, zeroed on <see cref="Dispose"/>. It never reveals itself through
/// <see cref="ToString"/>; use it only inside <see cref="Use{T}"/> / <see cref="UseAsString{T}"/> and dispose it at once.
/// </summary>
public sealed class SecretValue : IDisposable
{
    private readonly byte[] _buffer;
    private bool _disposed;

    private SecretValue(byte[] buffer) => _buffer = buffer;

    /// <summary>Gets the length in bytes.</summary>
    public int Length => _buffer.Length;

    /// <summary>Copies <paramref name="value"/> into a new pinned buffer.</summary>
    /// <param name="value">The bytes.</param>
    /// <returns>The value; the caller owns it.</returns>
    public static SecretValue Copy(ReadOnlySpan<byte> value)
    {
        var buffer = GC.AllocateArray<byte>(value.Length, pinned: true);
        value.CopyTo(buffer);
        return new SecretValue(buffer);
    }

    /// <summary>Takes ownership of a pinned buffer (no copy).</summary>
    internal static SecretValue Own(byte[] pinnedBuffer) => new(pinnedBuffer);

    /// <summary>Allocates a pinned buffer of <paramref name="length"/> bytes for decryption.</summary>
    internal static byte[] AllocatePinned(int length) => GC.AllocateArray<byte>(length, pinned: true);

    /// <summary>Uses the bytes.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="use">The function; it must not keep the span.</param>
    /// <returns>Its result.</returns>
    public T Use<T>(Func<ReadOnlySpan<byte>, T> use)
    {
        ArgumentNullException.ThrowIfNull(use);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return use(_buffer);
    }

    /// <summary>Uses the value decoded as UTF-8 (the string is not cached; strings cannot be zeroed, keep its use short).</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="use">The function.</param>
    /// <returns>Its result.</returns>
    public T UseAsString<T>(Func<string, T> use)
    {
        ArgumentNullException.ThrowIfNull(use);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return use(Encoding.UTF8.GetString(_buffer));
    }

    /// <summary>Never reveals the value.</summary>
    /// <returns><c>***</c>.</returns>
    public override string ToString() => "***";

    /// <summary>Zeroes the buffer.</summary>
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_buffer);
        _disposed = true;
    }

    /// <summary>Test hook (SEC-U-010): the internal buffer, also after disposal.</summary>
    internal ReadOnlySpan<byte> RawBufferForTests => _buffer;
}
