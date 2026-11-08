using System.Security.Cryptography;
using System.Text;

namespace Watchpoint.Integration.Abstractions.Context;

/// <summary>Access to the connection's secrets (INT §7.3, §8).</summary>
public interface ISecretAccessor
{
    /// <summary>Gets a secret; dispose it as soon as it was used.</summary>
    /// <param name="fieldId">A field id declared in the manifest's secrets schema.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The secret, or <see langword="null"/> when the field is not set.</returns>
    ValueTask<ConnectorSecret?> GetAsync(string fieldId, CancellationToken ct);
}

/// <summary>
/// A secret value. It is never printed (<see cref="ToString"/> returns <c>***</c>) and its bytes are zeroed on
/// <see cref="Dispose"/>; afterwards every use throws <see cref="ObjectDisposedException"/>. Strings created by
/// <see cref="UseAsString{T}"/> cannot be zeroed: prefer <see cref="UseBytes{T}"/>.
/// </summary>
public sealed class ConnectorSecret : IDisposable
{
    private readonly Lock _lock = new();
    private readonly byte[] _value;
    private bool _disposed;

    /// <summary>Initialises a secret from its UTF-8 bytes (copied).</summary>
    /// <param name="utf8Value">The value.</param>
    public ConnectorSecret(ReadOnlySpan<byte> utf8Value) => _value = utf8Value.ToArray();

    /// <summary>Uses the value as a string.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="use">The use; must not keep the string.</param>
    /// <returns>The result of <paramref name="use"/>.</returns>
    public T UseAsString<T>(Func<string, T> use)
    {
        ArgumentNullException.ThrowIfNull(use);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return use(Encoding.UTF8.GetString(_value));
        }
    }

    /// <summary>Uses the value as UTF-8 bytes.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="use">The use; must not keep the span.</param>
    /// <returns>The result of <paramref name="use"/>.</returns>
    public T UseBytes<T>(Func<ReadOnlySpan<byte>, T> use)
    {
        ArgumentNullException.ThrowIfNull(use);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return use(_value);
        }
    }

    /// <summary>Returns <c>***</c>.</summary>
    /// <returns><c>***</c>.</returns>
    public override string ToString() => "***";

    /// <summary>Zeroes the value.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            CryptographicOperations.ZeroMemory(_value);
            _disposed = true;
        }
    }

    /// <summary>Whether every byte of the stored value is zero (tests).</summary>
    internal bool IsZeroed => _value.All(b => b == 0);
}
