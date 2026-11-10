using Microsoft.EntityFrameworkCore;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal.Storage;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Internal.Passwords;

/// <summary>Password policy validator (spec IDN §5).</summary>
internal sealed class PasswordPolicy : IPasswordPolicy
{
    private readonly PasswordPolicyOptions _options;
    private readonly IDbContextFactory<IdentityDbContext>? _dbFactory;
    private readonly Argon2PasswordHasher? _hasher;

    public PasswordPolicy(
        PasswordPolicyOptions? options = null,
        IDbContextFactory<IdentityDbContext>? dbFactory = null,
        Argon2PasswordHasher? hasher = null)
    {
        _options = options ?? new PasswordPolicyOptions();
        _dbFactory = dbFactory;
        _hasher = hasher;
    }

    public async ValueTask<Result> ValidateAsync(string password, string login, string displayName, UserId? userId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password) || password.Length < _options.MinLength)
        {
            return IdentityErrors.PasswordTooShort(_options.MinLength);
        }

        // Composition: >= 3 of {lower, upper, digit, symbol}, OR length >= 16 (passphrases)
        if (password.Length < 16)
        {
            var classes = 0;
            if (password.Any(char.IsLower)) classes++;
            if (password.Any(char.IsUpper)) classes++;
            if (password.Any(char.IsDigit)) classes++;
            if (password.Any(ch => !char.IsLetterOrDigit(ch))) classes++;

            if (classes < 3)
            {
                return IdentityErrors.PasswordTooSimple();
            }
        }

        // Must not contain login or display name parts (>= 4 chars), case-insensitive
        if (_options.RejectLoginDisplayNameParts)
        {
            if (ContainsSubstrings(password, login) || ContainsSubstrings(password, displayName))
            {
                return IdentityErrors.PasswordContainsLogin();
            }
        }

        // Breached list match (case-insensitive exact match)
        if (_options.CheckBreachedList && BreachedPasswords.IsBreached(password))
        {
            return IdentityErrors.PasswordBreached();
        }

        // History: Last N hashes rejected
        if (userId is { } uid && _options.HistoryCount > 0 && _dbFactory is not null && _hasher is not null)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var historyEntities = await db.PasswordHistory
                .Where(h => h.UserId == uid)
                .ToListAsync(ct).ConfigureAwait(false);

            var history = historyEntities
                .OrderByDescending(h => h.CreatedAt)
                .Take(_options.HistoryCount)
                .Select(h => h.Hash)
                .ToList();

            foreach (var previousHash in history)
            {
                var (matched, _) = await _hasher.VerifyPasswordAsync(password, previousHash, ct).ConfigureAwait(false);
                if (matched)
                {
                    return IdentityErrors.PasswordReused(_options.HistoryCount);
                }
            }
        }

        return Result.Success();
    }

    private static bool ContainsSubstrings(string password, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length < 4)
        {
            return false;
        }

        var normalizedCandidate = candidate.Trim();
        // Check for any substring of candidate of length >= 4
        for (var len = 4; len <= normalizedCandidate.Length; len++)
        {
            for (var i = 0; i <= normalizedCandidate.Length - len; i++)
            {
                var sub = normalizedCandidate.Substring(i, len);
                if (password.Contains(sub, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
