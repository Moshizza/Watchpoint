using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Konscious.Security.Cryptography;
using Watchpoint.Identity.Contracts;

namespace Watchpoint.Identity.Internal.Passwords;

/// <summary>Argon2id password hasher and verifier (spec IDN §5).</summary>
internal sealed partial class Argon2PasswordHasher
{
    /// <summary>Maximum password length in UTF-16 code units, checked before and after normalisation (spec IDN §5, §14).</summary>
    public const int MaxPasswordLength = 256;

    // Upper bounds accepted from a stored PHC string, so that a tampered row cannot make verification allocate
    // gigabytes or accept a truncated hash (spec IDN §5).
    private const int MaxMemoryKiB = 1 << 20;
    private const int MaxIterations = 16;
    private const int MaxParallelism = 16;
    private const int MinSaltSize = 8;
    private const int MinHashSize = 16;
    private const int MaxSaltOrHashSize = 64;

    private static readonly SemaphoreSlim _sharedPool = new(4, 4);
    private readonly SemaphoreSlim _pool;
    private readonly TimeSpan _poolWait;
    private readonly Argon2Parameters _parameters;

    public Argon2PasswordHasher(Argon2Parameters? parameters = null)
        : this(parameters, null, null)
    {
    }

    /// <summary>Test constructor: a private pool and wait time (production uses the process-wide pool of 4 and 5 s).</summary>
    internal Argon2PasswordHasher(Argon2Parameters? parameters, SemaphoreSlim? pool, TimeSpan? poolWait)
    {
        _parameters = parameters ?? new Argon2Parameters();
        _pool = pool ?? _sharedPool;
        _poolWait = poolWait ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Normalises a password to Unicode NFKC so that the same password typed on different keyboards or platforms
    /// gives the same bytes (NIST SP 800-63B §5.1.1.2). Returns false for an empty password, one longer than
    /// <see cref="MaxPasswordLength"/> (before or after normalisation) or one that is not valid UTF-16.
    /// </summary>
    public static bool TryNormalize(string? password, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(password) || password.Length > MaxPasswordLength)
        {
            return false;
        }

        try
        {
            normalized = password.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Lone surrogates: not a valid Unicode string.
            return false;
        }

        return normalized.Length is > 0 and <= MaxPasswordLength;
    }

    /// <summary>Default production parameters (spec IDN §5: 64 MiB, t=3, p=2).</summary>
    public static readonly Argon2Parameters DefaultParameters = new(65536, 3, 2, 16, 32);

    /// <summary>Fast test parameters for fast unit testing.</summary>
    public static readonly Argon2Parameters TestParameters = new(1024, 1, 1, 16, 32);

    /// <summary>Hashes a password with Argon2id using configured parameters.</summary>
    public async Task<string> HashPasswordAsync(string password, CancellationToken ct = default)
    {
        if (!TryNormalize(password, out var normalized))
        {
            // The message never contains the password.
            throw new ArgumentException("Password is empty, longer than 256 characters or not valid Unicode.", nameof(password));
        }

        password = normalized;
        var salt = RandomNumberGenerator.GetBytes(_parameters.SaltSize);
        var hash = await ComputeHashAsync(password, salt, _parameters.MemorySizeKiB, _parameters.Iterations, _parameters.Parallelism, _parameters.HashSize, ct).ConfigureAwait(false);

        // PHC string format: $argon2id$v=19$m=65536,t=3,p=2$<salt>$<hash> (base64url without padding)
        var saltB64 = Base64UrlEncode(salt);
        var hashB64 = Base64UrlEncode(hash);

        return $"$argon2id$v=19$m={_parameters.MemorySizeKiB},t={_parameters.Iterations},p={_parameters.Parallelism}${saltB64}${hashB64}";
    }

    /// <summary>Verifies a password against a PHC string and detects whether rehashing is needed.</summary>
    public async Task<(bool Verified, bool NeedsRehash)> VerifyPasswordAsync(string password, string phcHash, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(phcHash) || !TryNormalize(password, out var normalized))
        {
            return (false, false);
        }

        password = normalized;

        if (!TryParsePhc(phcHash, out var memoryKiB, out var iterations, out var parallelism, out var salt, out var expectedHash))
        {
            return (false, false);
        }

        var computed = await ComputeHashAsync(password, salt, memoryKiB, iterations, parallelism, expectedHash.Length, ct).ConfigureAwait(false);
        var verified = CryptographicOperations.FixedTimeEquals(computed, expectedHash);

        var needsRehash = verified && (memoryKiB < _parameters.MemorySizeKiB || iterations < _parameters.Iterations || parallelism < _parameters.Parallelism
            || salt.Length < _parameters.SaltSize || expectedHash.Length < _parameters.HashSize);
        return (verified, needsRehash);
    }

    /// <summary>Executes dummy hash verification for constant-time behavior on unknown users.</summary>
    public async Task VerifyDummyAsync(string? password, CancellationToken ct = default)
    {
        // Same work whatever the input: an invalid password (empty, too long, bad Unicode) is replaced by a fixed
        // one so that this path costs the same as a real verification.
        if (!TryNormalize(password, out var normalized))
        {
            normalized = "dummy-password";
        }

        password = normalized;
        var dummySalt = new byte[16];
        await ComputeHashAsync(password, dummySalt, _parameters.MemorySizeKiB, _parameters.Iterations, _parameters.Parallelism, 32, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> ComputeHashAsync(string password, byte[] salt, int memoryKiB, int iterations, int parallelism, int hashSize, CancellationToken ct)
    {
        // Pool bounded to max 4 concurrent hashes; wait up to 5 seconds
        var acquired = await _pool.WaitAsync(_poolWait, ct).ConfigureAwait(false);
        if (!acquired)
        {
            throw new HashPoolBusyException();
        }

        try
        {
            var passwordBytes = Encoding.UTF8.GetBytes(password);
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                MemorySize = memoryKiB,
                Iterations = iterations,
                DegreeOfParallelism = parallelism,
            };

            return await Task.Run(() => argon2.GetBytes(hashSize), ct).ConfigureAwait(false);
        }
        finally
        {
            _pool.Release();
        }
    }

    private static bool TryParsePhc(string phc, out int memoryKiB, out int iterations, out int parallelism, out byte[] salt, out byte[] hash)
    {
        memoryKiB = 0;
        iterations = 0;
        parallelism = 0;
        salt = [];
        hash = [];

        var match = PhcRegex().Match(phc);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["m"].Value, out memoryKiB) ||
            !int.TryParse(match.Groups["t"].Value, out iterations) ||
            !int.TryParse(match.Groups["p"].Value, out parallelism))
        {
            return false;
        }

        try
        {
            salt = Base64UrlDecode(match.Groups["salt"].Value);
            hash = Base64UrlDecode(match.Groups["hash"].Value);
        }
        catch (FormatException)
        {
            return false;
        }

        return memoryKiB is >= 8 and <= MaxMemoryKiB
            && iterations is >= 1 and <= MaxIterations
            && parallelism is >= 1 and <= MaxParallelism
            && memoryKiB >= 8 * parallelism
            && salt.Length is >= MinSaltSize and <= MaxSaltOrHashSize
            && hash.Length is >= MinHashSize and <= MaxSaltOrHashSize;
    }

    private static string Base64UrlEncode(byte[] input)
    {
        var base64 = Convert.ToBase64String(input);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var base64 = input.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return Convert.FromBase64String(base64);
    }

    [GeneratedRegex(@"^\$argon2id\$v=19\$m=(?<m>\d{1,9}),t=(?<t>\d{1,9}),p=(?<p>\d{1,9})\$(?<salt>[A-Za-z0-9_-]+)\$(?<hash>[A-Za-z0-9_-]+)$")]
    private static partial Regex PhcRegex();
}

/// <summary>
/// The bounded hash pool stayed full for the whole wait (spec IDN §5); callers map it to <c>common.rate_limited</c>.
/// </summary>
internal sealed class HashPoolBusyException : Exception
{
    public HashPoolBusyException()
        : base("The password hash pool is busy.")
    {
    }

    public HashPoolBusyException(string message)
        : base(message)
    {
    }

    public HashPoolBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
