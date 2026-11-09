using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Konscious.Security.Cryptography;
using Watchpoint.Identity.Contracts;

namespace Watchpoint.Identity.Internal.Passwords;

/// <summary>Argon2id password hasher and verifier (spec IDN §5).</summary>
internal sealed partial class Argon2PasswordHasher
{
    private static readonly SemaphoreSlim _pool = new(4, 4);
    private readonly Argon2Parameters _parameters;

    public Argon2PasswordHasher(Argon2Parameters? parameters = null)
    {
        _parameters = parameters ?? new Argon2Parameters();
    }

    /// <summary>Default production parameters (spec IDN §5: 64 MiB, t=3, p=2).</summary>
    public static readonly Argon2Parameters DefaultParameters = new(65536, 3, 2, 16, 32);

    /// <summary>Fast test parameters for fast unit testing.</summary>
    public static readonly Argon2Parameters TestParameters = new(1024, 1, 1, 16, 32);

    /// <summary>Hashes a password with Argon2id using configured parameters.</summary>
    public async Task<string> HashPasswordAsync(string password, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (password.Length > 256)
        {
            throw new ArgumentException("Password exceeds 256 characters limit before hashing.", nameof(password));
        }

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
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(phcHash))
        {
            return (false, false);
        }

        if (password.Length > 256)
        {
            return (false, false);
        }

        if (!TryParsePhc(phcHash, out var memoryKiB, out var iterations, out var parallelism, out var salt, out var expectedHash))
        {
            return (false, false);
        }

        var computed = await ComputeHashAsync(password, salt, memoryKiB, iterations, parallelism, expectedHash.Length, ct).ConfigureAwait(false);
        var verified = CryptographicOperations.FixedTimeEquals(computed, expectedHash);

        var needsRehash = verified && (memoryKiB < _parameters.MemorySizeKiB || iterations < _parameters.Iterations || parallelism < _parameters.Parallelism);
        return (verified, needsRehash);
    }

    /// <summary>Executes dummy hash verification for constant-time behavior on unknown users.</summary>
    public async Task VerifyDummyAsync(string password, CancellationToken ct = default)
    {
        var dummySalt = new byte[16];
        await ComputeHashAsync(password, dummySalt, _parameters.MemorySizeKiB, _parameters.Iterations, _parameters.Parallelism, 32, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ComputeHashAsync(string password, byte[] salt, int memoryKiB, int iterations, int parallelism, int hashSize, CancellationToken ct)
    {
        // Pool bounded to max 4 concurrent hashes; wait up to 5 seconds
        var acquired = await _pool.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (!acquired)
        {
            throw new TimeoutException("Hash pool wait timeout exceeded (5s).");
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
            return true;
        }
        catch
        {
            return false;
        }
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

    [GeneratedRegex(@"^\$argon2id\$v=19\$m=(?<m>\d+),t=(?<t>\d+),p=(?<p>\d+)\$(?<salt>[A-Za-z0-9_-]+)\$(?<hash>[A-Za-z0-9_-]+)$")]
    private static partial Regex PhcRegex();
}
