using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Watchpoint.Api.Contracts;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Api.Internal.Accounts;

/// <summary>
/// IDN endpoints (spec IDN §7.2) on the framework. They live in the API project because the API depends on IDN (token
/// validation): sign-in, refresh, sign-out, change password, me, MFA verification and enrolment (TOTP), sessions and
/// user reads. User changes and the self-service MFA endpoints come with their IDN tasks.
/// </summary>
internal static class AuthEndpoints
{
    public const string UsersView = "config.users.view";

    public const string SessionsView = "admin.sessions.view";

    public const string SessionsManage = "admin.sessions.manage";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/v1/auth").WithTags("Auth");
        auth.MapPost("/sign-in", SignInAsync).AllowAnonymous().WithName("Auth_SignIn").WithSummary("Sign in with login and password");
        auth.MapPost("/refresh", RefreshAsync).AllowAnonymous().WithName("Auth_RefreshTokens").WithSummary("Exchange a refresh token for a new pair");
        auth.MapPost("/sign-out", SignOutAsync).RequireAuthentication().WithName("Auth_SignOut").WithSummary("Revoke the current session");
        auth.MapPost("/change-password", ChangePasswordAsync).AllowAnonymous().WithName("Auth_ChangePassword")
            .WithSummary("Change the password (access token with current password, or changeToken)");
        auth.MapGet("/me", MeAsync).RequireAuthentication().WithName("Auth_GetMe").WithSummary("Current user, session and permissions");
        auth.MapPost("/mfa/verify", VerifyMfaAsync).AllowAnonymous().WithName("Auth_VerifyMfa").WithSummary("Complete a sign-in with a second factor");
        auth.MapPost("/mfa/enrol/totp", BeginEnrolmentAsync).AllowAnonymous().WithName("Auth_BeginTotpEnrolment").WithSummary("Start the TOTP enrolment of a sign-in");
        auth.MapPost("/mfa/enrol/totp/confirm", CompleteEnrolmentAsync).AllowAnonymous().WithName("Auth_ConfirmTotpEnrolment")
            .WithSummary("Confirm the TOTP enrolment → tokens and recovery codes");

        var sessions = endpoints.MapGroup("/api/v1/sessions").WithTags("Sessions");
        sessions.MapGet("/", ListSessionsAsync).RequireAuthentication().WithName("Sessions_ListSessions").WithSummary("Active sessions (own; others with admin.sessions.view)");
        sessions.MapDelete("/{id:guid}", RevokeSessionAsync).RequireAuthentication().WithName("Sessions_RevokeSession")
            .WithSummary("Revoke a session (own; others with admin.sessions.manage)");

        var users = endpoints.MapGroup("/api/v1/users").WithTags("Users").RequirePermission(UsersView);
        users.MapGet("/", ListUsers).WithPaging().WithName("Users_ListUsers").WithSummary("Users");
        users.MapGet("/{id:guid}", GetUser).WithName("Users_GetUser").WithSummary("One user");
    }

    internal sealed record SignInBody(string? Login, string? Password, string? ClientKind, string? ClientInstanceId, string? WorkstationId);

    internal sealed record RefreshBody(string? RefreshToken, string? ClientKind, string? ClientInstanceId, string? WorkstationId);

    internal sealed record ChangePasswordBody(string? Current, string? New, string? ChangeToken);

    internal sealed record MfaVerifyBody(string? MfaToken, string? Method, string? ClientKind, string? ClientInstanceId, string? Code, string? Assertion);

    internal sealed record EnrolmentBody(string? EnrolmentToken, string? Code, string? ClientKind, string? ClientInstanceId);

    private static async Task<IResult> SignInAsync(SignInBody body, HttpContext http, [FromServices] IAuthenticationService authentication)
    {
        var fields = new List<FieldError>();
        Required(body.Login, "/login", fields);
        Required(body.Password, "/password", fields);
        var client = Client(http, body.ClientKind, body.ClientInstanceId, body.WorkstationId, fields);
        if (fields.Count > 0)
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed(fields));
        }

        var outcome = await authentication.SignInLocalAsync(new SignInRequest(body.Login!, body.Password!, client!), http.RequestAborted).ConfigureAwait(false);
        return HttpResults.From(outcome, Outcome);
    }

    private static async Task<IResult> RefreshAsync(RefreshBody body, HttpContext http, [FromServices] IAuthenticationService authentication)
    {
        var fields = new List<FieldError>();
        Required(body.RefreshToken, "/refreshToken", fields);
        var client = Client(http, body.ClientKind, body.ClientInstanceId, body.WorkstationId, fields);
        if (fields.Count > 0)
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed(fields));
        }

        var pair = await authentication.RefreshAsync(body.RefreshToken!, client!, http.RequestAborted).ConfigureAwait(false);
        return HttpResults.From(pair, p => ApiResults.Ok(TokensDto.Of(p, null)));
    }

    private static async Task<IResult> SignOutAsync(HttpContext http, [FromServices] IAuthenticationService authentication) =>
        HttpResults.From(await authentication.SignOutAsync(http.CurrentUser().SessionId, http.RequestAborted).ConfigureAwait(false));

    private static async Task<IResult> ChangePasswordAsync(ChangePasswordBody body, HttpContext http, [FromServices] IAuthenticationService authentication, [FromServices] IUserDirectory users)
    {
        if (string.IsNullOrEmpty(body.New))
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed([FieldError.Create("/new", CommonErrorCodes.ValueRequired)]));
        }

        if (!string.IsNullOrEmpty(body.ChangeToken))
        {
            var outcome = await authentication.CompletePasswordChangeAsync(body.ChangeToken, body.New, http.RequestAborted).ConfigureAwait(false);
            return HttpResults.From(outcome, Outcome);
        }

        var user = http.CurrentUser();
        if (!user.IsAuthenticated)
        {
            return HttpResults.Problem(CommonErrors.Unauthenticated());
        }

        if (string.IsNullOrEmpty(body.Current))
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed([FieldError.Create("/current", CommonErrorCodes.ValueRequired)]));
        }

        return HttpResults.From(await users.ChangeOwnPasswordAsync(user.UserId, body.Current, body.New, user.SessionId, http.RequestAborted).ConfigureAwait(false));
    }

    private static async Task<IResult> MeAsync(HttpContext http, [FromServices] ICurrentUserService current)
    {
        var claims = ((Auth.CurrentUser)http.CurrentUser()).Claims!;
        var me = await current.GetAsync(claims, http.RequestAborted).ConfigureAwait(false);
        return HttpResults.From(me, m => ApiResults.Ok(new MeDto(UserDto.Of(m.User), SessionDto.Of(m.Session), m.Permissions,
            new MfaDto(m.Mfa.TotpEnrolled, m.Mfa.WebAuthnCredentials.Count, m.Mfa.RecoveryCodesRemaining))));
    }

    private static async Task<IResult> VerifyMfaAsync(MfaVerifyBody body, HttpContext http, [FromServices] IAuthenticationService authentication)
    {
        var fields = new List<FieldError>();
        Required(body.MfaToken, "/mfaToken", fields);
        MfaMethod method = default;
        if (!Enum.TryParse(body.Method, ignoreCase: true, out method) || !Enum.IsDefined(method))
        {
            fields.Add(FieldError.Create("/method", CommonErrorCodes.ValueNotAllowed));
        }

        var client = Client(http, body.ClientKind, body.ClientInstanceId, null, fields);
        if (fields.Count > 0)
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed(fields));
        }

        var pair = await authentication.VerifyMfaAsync(new MfaVerification(body.MfaToken!, method, client!, body.Code, body.Assertion), http.RequestAborted).ConfigureAwait(false);
        return HttpResults.From(pair, p => ApiResults.Ok(TokensDto.Of(p, null)));
    }

    private static async Task<IResult> BeginEnrolmentAsync(EnrolmentBody body, HttpContext http, [FromServices] IAuthenticationService authentication)
    {
        var fields = new List<FieldError>();
        Required(body.EnrolmentToken, "/enrolmentToken", fields);
        var client = Client(http, body.ClientKind, body.ClientInstanceId, null, fields);
        if (fields.Count > 0)
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed(fields));
        }

        var enrolment = await authentication.BeginMfaEnrolmentAsync(body.EnrolmentToken!, client!, http.RequestAborted).ConfigureAwait(false);
        return HttpResults.From(enrolment, e => ApiResults.Ok(new { secret = e.SecretBase32, otpAuthUri = e.OtpAuthUri.ToString() }));
    }

    private static async Task<IResult> CompleteEnrolmentAsync(EnrolmentBody body, HttpContext http, [FromServices] IAuthenticationService authentication)
    {
        var fields = new List<FieldError>();
        Required(body.EnrolmentToken, "/enrolmentToken", fields);
        Required(body.Code, "/code", fields);
        var client = Client(http, body.ClientKind, body.ClientInstanceId, null, fields);
        if (fields.Count > 0)
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed(fields));
        }

        var completed = await authentication.CompleteMfaEnrolmentAsync(body.EnrolmentToken!, body.Code!, client!, http.RequestAborted).ConfigureAwait(false);
        return HttpResults.From(completed, c => ApiResults.Ok(new { tokens = TokensDto.Of(c.Tokens, null), recoveryCodes = c.RecoveryCodes.Codes }));
    }

    private static async Task<IResult> ListSessionsAsync(Guid? user, HttpContext http, [FromServices] ISessionManagement sessions)
    {
        var current = http.CurrentUser();
        var list = await sessions.ListAsync(current.UserId, user is { } id ? UserId.From(id) : current.UserId, http.RequestAborted).ConfigureAwait(false);
        return HttpResults.From(list, l => ApiResults.Page(l.Select(SessionDto.Of).ToList(), null));
    }

    private static async Task<IResult> RevokeSessionAsync(Guid id, HttpContext http, [FromServices] ISessionManagement sessions) =>
        HttpResults.From(await sessions.RevokeAsync(http.CurrentUser().UserId, SessionId.From(id), "api", http.RequestAborted).ConfigureAwait(false));

    private static IResult ListUsers(HttpContext http, string? text, [FromServices] IUserDirectory users) =>
        HttpResults.From(http.Paging(), p =>
        {
            var page = users.Query(new UserQuery(Text: text), p.Page);
            return ApiResults.Page(page.Items.Select(UserDto.Of).ToList(), page.NextCursor);
        });

    private static IResult GetUser(Guid id, [FromServices] IUserDirectory users) =>
        HttpResults.From(users.Get(UserId.From(id)), u => HttpResults.Versioned(UserDto.Of(u), u.Version));

    private static IResult Outcome(SignInOutcome outcome) => outcome switch
    {
        SignInOutcome.Signed signed => ApiResults.Ok(TokensDto.Of(signed.Tokens, UserDto.Of(signed.User))),
        SignInOutcome.MfaRequired mfa => ApiResults.Ok(new { next = "mfa", mfaToken = mfa.MfaToken, methods = mfa.Methods }),
        SignInOutcome.PasswordChangeRequired change => ApiResults.Ok(new { next = "changePassword", changeToken = change.ChangeToken }),
        SignInOutcome.MfaEnrolmentRequired enrol => ApiResults.Ok(new { next = "mfaEnrolment", enrolmentToken = enrol.EnrolmentToken }),
        _ => HttpResults.Problem(Error.Internal("unknown sign-in outcome")),
    };

    private static void Required(string? value, string path, List<FieldError> fields)
    {
        if (string.IsNullOrEmpty(value))
        {
            fields.Add(FieldError.Create(path, CommonErrorCodes.ValueRequired));
        }
    }

    private static ClientContext? Client(HttpContext http, string? kind, string? instanceId, string? workstationId, List<FieldError> fields)
    {
        if (!Enum.TryParse<ClientKind>(kind, ignoreCase: true, out var clientKind) || !Enum.IsDefined(clientKind) || int.TryParse(kind, out _))
        {
            fields.Add(FieldError.Create("/clientKind", CommonErrorCodes.ValueNotAllowed));
            return null;
        }

        if (string.IsNullOrWhiteSpace(instanceId) || instanceId.Length > 128)
        {
            fields.Add(FieldError.Create("/clientInstanceId", CommonErrorCodes.ValueRequired));
            return null;
        }

        return new ClientContext(clientKind, instanceId, workstationId, http.Connection.RemoteIpAddress,
            http.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null);
    }

    internal sealed record TokensDto(string AccessToken, DateTimeOffset AccessExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt, Guid SessionId, UserDto? User)
    {
        public static TokensDto Of(TokenPair pair, UserDto? user) =>
            new(pair.AccessToken, pair.AccessExpiresAt, pair.RefreshToken, pair.RefreshExpiresAt, pair.Session.Value, user);
    }

    /// <summary>User JSON (IDN §7.2): never password hashes, TOTP secrets or recovery codes.</summary>
    internal sealed record UserDto(Guid Id, string Login, string DisplayName, string? Email, string? Phone, string Language, string? TimeZone, string Kind,
        bool Enabled, bool Administrator, bool MustChangePassword, DateTimeOffset? LastSignInAt, long Version)
    {
        public static UserDto Of(User u) => new(u.Id.Value, u.Login, u.DisplayName, u.Email, u.Phone, u.Language, u.TimeZone,
            System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(u.Kind.ToString()), u.Enabled, u.Administrator, u.MustChangePassword, u.LastSignInAt, u.Version);
    }

    internal sealed record SessionDto(Guid Id, Guid UserId, string ClientKind, string ClientInstanceId, string? WorkstationId, string? RemoteIp,
        DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, DateTimeOffset ExpiresAt)
    {
        public static SessionDto Of(Session s) => new(s.Id.Value, s.UserId.Value, s.ClientKind.ToString().ToLowerInvariant(), s.ClientInstanceId,
            s.WorkstationId, s.RemoteIp, s.CreatedAt, s.LastActivityAt, s.ExpiresAt);
    }

    internal sealed record MfaDto(bool TotpEnrolled, int WebAuthnCredentials, int RecoveryCodesRemaining);

    internal sealed record MeDto(UserDto User, SessionDto Session, IReadOnlyList<string> Permissions, MfaDto Mfa);
}
