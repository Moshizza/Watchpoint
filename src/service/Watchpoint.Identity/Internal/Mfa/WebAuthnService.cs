using System.Collections.Concurrent;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal.Storage;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Identity.Internal.Mfa;

/// <summary>
/// WebAuthn registration and assertion ceremonies with Fido2NetLib (spec IDN §10). RP id and origins come from the
/// settings and are verified strictly by the library (challenge, origin, RP id hash, signature, user presence);
/// challenges are single use and expire after five minutes (TimeProvider); the signature counter must grow.
/// </summary>
internal sealed class WebAuthnService
{
    internal static readonly TimeSpan CeremonyLifetime = TimeSpan.FromMinutes(5);
    private static readonly Error _invalid = Error.Create(ErrorKind.Unauthenticated, "identity.webauthn.invalid");

    private readonly IDbContextFactory<IdentityDbContext> _dbFactory;
    private readonly TimeProvider _clock;
    private readonly WebAuthnOptions _options;
    private readonly Fido2? _fido2;

    // Pending ceremonies keyed by the base64url challenge found in the authenticator's clientDataJSON.
    private readonly ConcurrentDictionary<string, Ceremony> _ceremonies = new();

    private sealed record Ceremony(UserId UserId, bool Registration, string OptionsJson, DateTimeOffset ExpiresAt);

    public WebAuthnService(IDbContextFactory<IdentityDbContext> dbFactory, TimeProvider clock, WebAuthnOptions? options = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new WebAuthnOptions();
        if (_options.IsEnabled)
        {
            _fido2 = new Fido2(new Fido2Configuration
            {
                RPID = _options.RpId,
                RPName = "Watchpoint",
                Origins = _options.Origins.ToHashSet(StringComparer.OrdinalIgnoreCase),
                Timeout = (uint)CeremonyLifetime.TotalMilliseconds,
            });
        }
    }

    public bool IsEnabled => _fido2 is not null;

    public async Task<IReadOnlyList<WebAuthnCredentialInfo>> ListAsync(UserId userId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.WebAuthnCredentials.AsNoTracking().Where(c => c.UserId == userId).ToListAsync(ct).ConfigureAwait(false);
        return rows.OrderBy(r => r.CreatedAt).Select(Map).ToList();
    }

    public async Task<Result<WebAuthnCeremony>> BeginRegistrationAsync(User user, CancellationToken ct)
    {
        if (_fido2 is null)
        {
            return IdentityErrors.WebAuthnNotConfigured();
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.WebAuthnCredentials.AsNoTracking().Where(c => c.UserId == user.Id).Select(c => c.CredentialId).ToListAsync(ct).ConfigureAwait(false);

        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = user.Id.Value.ToByteArray(), Name = user.Login, DisplayName = user.DisplayName },
            ExcludeCredentials = existing.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                UserVerification = UserVerificationRequirement.Preferred,
                ResidentKey = ResidentKeyRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        return Store(user.Id, registration: true, options.Challenge, options.ToJson());
    }

    public async Task<Result<WebAuthnCredentialInfo>> CompleteRegistrationAsync(UserId userId, string attestationResponseJson, string name, CancellationToken ct)
    {
        if (_fido2 is null)
        {
            return IdentityErrors.WebAuthnNotConfigured();
        }

        name = (name ?? string.Empty).Trim();
        if (name.Length is 0 or > 64)
        {
            return Error.Validation("common.validation.failed", [FieldError.Create("/name", CommonErrorCodes.ValueTooLong, ("min", 1), ("max", 64))]);
        }

        if (!TryTakeCeremony(attestationResponseJson, userId, registration: true, out var ceremony) ||
            !TryDeserialize<AuthenticatorAttestationRawResponse>(attestationResponseJson, out var raw))
        {
            return _invalid;
        }

        var originalOptions = CredentialCreateOptions.FromJson(ceremony.OptionsJson);
        RegisteredPublicKeyCredential credential;
        try
        {
            credential = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = raw,
                OriginalOptions = originalOptions,
                IsCredentialIdUniqueToUserCallback = async (p, token) =>
                {
                    await using var db = await _dbFactory.CreateDbContextAsync(token).ConfigureAwait(false);
                    return !await db.WebAuthnCredentials.AnyAsync(c => c.CredentialId == p.CredentialId, token).ConfigureAwait(false);
                },
            }, ct).ConfigureAwait(false);
        }
        catch (Fido2VerificationException)
        {
            return _invalid;
        }

        var now = _clock.GetUtcNow();
        var entity = new WebAuthnCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            CredentialId = credential.Id,
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            TransportsJson = JsonSerializer.Serialize((credential.Transports ?? []).Select(t => t.ToString().ToLowerInvariant())),
            Name = name,
            AaGuid = credential.AaGuid,
            CreatedAt = now,
        };

        await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            db.WebAuthnCredentials.Add(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return Map(entity);
    }

    public async Task<Result<WebAuthnCeremony>> BeginAssertionAsync(UserId userId, CancellationToken ct)
    {
        if (_fido2 is null)
        {
            return IdentityErrors.WebAuthnNotConfigured();
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var credentials = await db.WebAuthnCredentials.AsNoTracking().Where(c => c.UserId == userId).Select(c => c.CredentialId).ToListAsync(ct).ConfigureAwait(false);
        if (credentials.Count == 0)
        {
            return IdentityErrors.MfaNotEnrolled();
        }

        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = credentials.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            UserVerification = UserVerificationRequirement.Preferred,
        });

        return Store(userId, registration: false, options.Challenge, options.ToJson());
    }

    /// <summary>Verifies an assertion of the user: challenge (single use), origin, RP id, signature and growing counter.</summary>
    public async Task<Result> VerifyAssertionAsync(UserId userId, string? assertionResponseJson, CancellationToken ct)
    {
        if (_fido2 is null)
        {
            return IdentityErrors.WebAuthnNotConfigured();
        }

        if (!TryTakeCeremony(assertionResponseJson, userId, registration: false, out var ceremony) ||
            !TryDeserialize<AuthenticatorAssertionRawResponse>(assertionResponseJson!, out var raw) || raw.RawId is null)
        {
            return _invalid;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var credential = await db.WebAuthnCredentials.FirstOrDefaultAsync(c => c.CredentialId == raw.RawId, ct).ConfigureAwait(false);
        if (credential is null || credential.UserId != userId)
        {
            return _invalid;
        }

        var stored = (uint)credential.SignCount;
        VerifyAssertionResult result;
        try
        {
            result = await _fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = raw,
                OriginalOptions = AssertionOptions.FromJson(ceremony.OptionsJson),
                StoredPublicKey = credential.PublicKey,
                StoredSignatureCounter = stored,
                IsUserHandleOwnerOfCredentialIdCallback = (p, _) =>
                    Task.FromResult(p.UserHandle is null || p.UserHandle.Length == 0 || p.UserHandle.AsSpan().SequenceEqual(userId.Value.ToByteArray())),
            }, ct).ConfigureAwait(false);
        }
        catch (Fido2VerificationException)
        {
            return _invalid;
        }

        // The library refuses a counter that did not grow; this keeps the rule explicit (a cloned authenticator replays
        // an old counter) and independent of the library's version.
        if ((stored != 0 || result.SignCount != 0) && result.SignCount <= stored)
        {
            return _invalid;
        }

        credential.SignCount = result.SignCount;
        credential.LastUsedAt = _clock.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (StorageException ex) when (ex.Error.Code.Value == "storage.concurrency.conflict")
        {
            // Another node verified an assertion of this credential meanwhile: this one is not counted twice.
            return _invalid;
        }

        return Result.Success();
    }

    public async Task<Result> RemoveAsync(UserId userId, Guid id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.WebAuthnCredentials.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return Error.NotFound("common.not_found", ("credentialId", id.ToString()));
        }

        db.WebAuthnCredentials.Remove(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<int> RemoveAllAsync(UserId userId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.WebAuthnCredentials.Where(c => c.UserId == userId).ToListAsync(ct).ConfigureAwait(false);
        db.WebAuthnCredentials.RemoveRange(rows);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        foreach (var pair in _ceremonies)
        {
            if (pair.Value.UserId == userId)
            {
                _ceremonies.TryRemove(pair.Key, out _);
            }
        }

        return rows.Count;
    }

    private WebAuthnCeremony Store(UserId userId, bool registration, byte[] challenge, string optionsJson)
    {
        var now = _clock.GetUtcNow();
        foreach (var pair in _ceremonies)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _ceremonies.TryRemove(pair.Key, out _);
            }
        }

        var expiresAt = now + CeremonyLifetime;
        _ceremonies[Base64Url(challenge)] = new Ceremony(userId, registration, optionsJson, expiresAt);
        return new WebAuthnCeremony(optionsJson, expiresAt);
    }

    // The challenge in clientDataJSON names the ceremony; it is taken out before verification (single use), and must
    // belong to this user and this kind of ceremony.
    private bool TryTakeCeremony(string? responseJson, UserId userId, bool registration, out Ceremony ceremony)
    {
        ceremony = null!;
        if (string.IsNullOrEmpty(responseJson) || responseJson.Length > 64 * 1024)
        {
            return false;
        }

        string? challenge;
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("response", out var response) ||
                !response.TryGetProperty("clientDataJSON", out var clientDataElement) ||
                clientDataElement.GetString() is not { } clientDataB64)
            {
                return false;
            }

            using var clientData = JsonDocument.Parse(FromBase64Url(clientDataB64));
            challenge = clientData.RootElement.TryGetProperty("challenge", out var c) ? c.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return false;
        }

        if (challenge is null || !_ceremonies.TryRemove(challenge.TrimEnd('='), out var found))
        {
            return false;
        }

        if (found.UserId != userId || found.Registration != registration || found.ExpiresAt <= _clock.GetUtcNow())
        {
            return false;
        }

        ceremony = found;
        return true;
    }

    private static bool TryDeserialize<T>(string json, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? value)
        where T : class
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json);
            return value is not null;
        }
        catch (JsonException)
        {
            value = null;
            return false;
        }
    }

    private static WebAuthnCredentialInfo Map(WebAuthnCredentialEntity e) =>
        new(e.Id, e.Name, JsonSerializer.Deserialize<List<string>>(e.TransportsJson) ?? [], e.CreatedAt, e.LastUsedAt);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
