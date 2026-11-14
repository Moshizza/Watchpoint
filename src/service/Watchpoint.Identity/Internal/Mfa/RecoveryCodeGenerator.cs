using System.Security.Cryptography;

namespace Watchpoint.Identity.Internal.Mfa;

/// <summary>
/// Recovery codes (spec IDN §10): ten codes of ten characters from an alphabet without look-alikes (no 0/O, 1/I),
/// 50 bits of entropy each, shown as <c>XXXXX-XXXXX</c>. Only the Argon2 hash of the normalised code is stored.
/// </summary>
internal static class RecoveryCodeGenerator
{
    public const int Count = 10;
    public const int Length = 10;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string[] Generate()
    {
        var codes = new string[Count];
        for (var i = 0; i < Count; i++)
        {
            var raw = RandomNumberGenerator.GetString(Alphabet, Length);
            codes[i] = raw[..5] + "-" + raw[5..];
        }

        return codes;
    }

    /// <summary>Upper case, no separators or spaces: what is hashed and compared.</summary>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[Math.Min(code.Length, 32)];
        var n = 0;
        foreach (var c in code)
        {
            if (c is '-' or ' ')
            {
                continue;
            }

            if (n == buffer.Length)
            {
                return string.Empty;
            }

            buffer[n++] = char.ToUpperInvariant(c);
        }

        return new string(buffer[..n]);
    }
}
