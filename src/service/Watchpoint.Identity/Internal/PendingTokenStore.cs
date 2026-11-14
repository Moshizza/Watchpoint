using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Identity.Internal;

/// <summary>What a pending token allows (spec IDN §6.1 step 7, §10).</summary>
internal enum PendingKind
{
    /// <summary>Set a new password (MustChangePassword).</summary>
    PasswordChange,

    /// <summary>Present a second factor.</summary>
    Mfa,

    /// <summary>Enrol TOTP because MFA is required and nothing is enrolled.</summary>
    MfaEnrolment,

    /// <summary>Step-up proof (bound to a session instead of a pending sign-in).</summary>
    StepUp,
}

/// <summary>A pending token: who, from which client, until when, how many wrong attempts so far.</summary>
internal sealed record PendingEntry(PendingKind Kind, UserId UserId, ClientContext Client, DateTimeOffset ExpiresAt, SessionId? Session = null)
{
    public int Failures { get; init; }
}

/// <summary>
/// The tokens of partial sign-ins and step-ups: 32 bytes from the CSPRNG, base64url, kept only as SHA-256; one live token
/// per user and kind (a new sign-in replaces the previous one); expired tokens are dropped in passing. A token is
/// nothing but a key of this map: it is not a JWT, not a refresh token, and no other service accepts it.
/// </summary>
internal sealed class PendingTokenStore
{
    private readonly ConcurrentDictionary<string, PendingEntry> _entries = new();

    public string Issue(PendingKind kind, UserId userId, ClientContext client, DateTimeOffset now, TimeSpan lifetime, SessionId? session = null)
    {
        RemoveAll(userId, kind, now, session);
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        _entries[Hash(token)] = new PendingEntry(kind, userId, client, now + lifetime, session);
        return token;
    }

    /// <summary>Looks a token up without consuming it.</summary>
    public bool TryPeek(string? token, PendingKind kind, DateTimeOffset now, out PendingEntry entry)
    {
        entry = null!;
        if (!IsPlausible(token))
        {
            return false;
        }

        if (!_entries.TryGetValue(Hash(token), out var found) || found.Kind != kind)
        {
            return false;
        }

        if (found.ExpiresAt <= now)
        {
            _entries.TryRemove(Hash(token), out _);
            return false;
        }

        entry = found;
        return true;
    }

    /// <summary>Takes a token out (single use).</summary>
    public bool TryTake(string? token, PendingKind kind, DateTimeOffset now, out PendingEntry entry)
    {
        entry = null!;
        if (!IsPlausible(token) || !_entries.TryRemove(Hash(token), out var found))
        {
            return false;
        }

        if (found.Kind != kind || found.ExpiresAt <= now)
        {
            return false;
        }

        entry = found;
        return true;
    }

    /// <summary>Puts a token back (a refused new password does not cost the change token).</summary>
    public void Restore(string token, PendingEntry entry) => _entries.TryAdd(Hash(token), entry);

    /// <summary>Counts a wrong attempt; returns the new count.</summary>
    public int RecordFailure(string token)
    {
        var key = Hash(token);
        while (_entries.TryGetValue(key, out var current))
        {
            var updated = current with { Failures = current.Failures + 1 };
            if (_entries.TryUpdate(key, updated, current))
            {
                return updated.Failures;
            }
        }

        return int.MaxValue;
    }

    public void Remove(string token) => _entries.TryRemove(Hash(token), out _);

    /// <summary>Removes the tokens of a user (of one kind, or all) and, in passing, every expired token.</summary>
    public void RemoveAll(UserId userId, PendingKind? kind, DateTimeOffset now, SessionId? session = null)
    {
        foreach (var pair in _entries)
        {
            var e = pair.Value;
            var sameOwner = e.UserId == userId && (kind is null || e.Kind == kind) && (session is null || e.Session == session);
            if (sameOwner || e.ExpiresAt <= now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <summary>Removes the step-up proofs of a session.</summary>
    public void RemoveSession(SessionId session)
    {
        foreach (var pair in _entries)
        {
            if (pair.Value.Session == session)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private static bool IsPlausible([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? token) =>
        !string.IsNullOrEmpty(token) && token.Length <= 128;

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
