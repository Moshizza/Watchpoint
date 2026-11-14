using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Watchpoint.Identity.Internal.Mfa;

/// <summary>
/// RFC 6238 time-based one-time passwords over RFC 4226 HOTP: HMAC-SHA1, 6 digits, 30-second steps (spec IDN §10).
/// Implemented here rather than through a package: the algorithm is forty lines, verified against the RFC test vectors
/// (IDN-U-130), and every comparison is constant time.
/// </summary>
internal static class Totp
{
    public const int SecretLength = 20;
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const int Window = 1;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>A new secret from the CSPRNG.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretLength);

    /// <summary>The RFC 6238 step of an instant.</summary>
    public static long StepOf(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>Computes the code of a step.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 6238 TOTP is defined over HMAC-SHA1 and authenticator apps implement that; the MAC is truncated to 6 digits and used once (spec IDN §10).")]
    public static string Compute(ReadOnlySpan<byte> secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> mac = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, mac);
        var offset = mac[19] & 0x0F;
        var binary = ((mac[offset] & 0x7F) << 24) | (mac[offset + 1] << 16) | (mac[offset + 2] << 8) | mac[offset + 3];
        var modulo = 1;
        for (var i = 0; i < digits; i++)
        {
            modulo *= 10;
        }

        return (binary % modulo).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>
    /// Verifies a code at <paramref name="now"/> within ±<see cref="Window"/> steps, refusing any step at or before
    /// <paramref name="lastUsedStep"/> (replay). Every candidate is computed and compared in constant time.
    /// </summary>
    /// <returns>The accepted step, or <see langword="null"/>.</returns>
    public static long? Verify(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now, long lastUsedStep)
    {
        if (code is null || code.Length != Digits || !code.All(char.IsAsciiDigit))
        {
            return null;
        }

        var presented = Encoding.ASCII.GetBytes(code);
        var current = StepOf(now);
        long? accepted = null;
        for (var delta = -Window; delta <= Window; delta++)
        {
            var step = current + delta;
            var candidate = Encoding.ASCII.GetBytes(Compute(secret, step));
            var matches = CryptographicOperations.FixedTimeEquals(candidate, presented);
            if (matches && step > lastUsedStep && accepted is null)
            {
                accepted = step;
            }
        }

        return accepted;
    }

    /// <summary>Base32 (RFC 4648, no padding) for authenticator apps.</summary>
    public static string ToBase32(ReadOnlySpan<byte> data)
    {
        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(Base32Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            result.Append(Base32Alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return result.ToString();
    }

    /// <summary>The <c>otpauth://totp/</c> URI shown as a QR code.</summary>
    public static Uri OtpAuthUri(string login, ReadOnlySpan<byte> secret)
    {
        var label = Uri.EscapeDataString("Watchpoint:" + login);
        return new Uri($"otpauth://totp/{label}?secret={ToBase32(secret)}&issuer=Watchpoint&algorithm=SHA1&digits={Digits}&period={StepSeconds}");
    }
}
