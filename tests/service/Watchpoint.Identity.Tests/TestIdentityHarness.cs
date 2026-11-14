using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal;
using Watchpoint.Identity.Internal.Audit;
using Watchpoint.Identity.Internal.Lockout;
using Watchpoint.Identity.Internal.Mfa;
using Watchpoint.Identity.Internal.Passwords;
using Watchpoint.Identity.Internal.Sessions;
using Watchpoint.Identity.Internal.Storage;
using Watchpoint.Identity.Internal.Tokens;
using Watchpoint.Kernel.Testing;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Identity.Tests;

internal sealed class TestIdentityHarness : IDisposable
{
    // Keeps the shared in-memory database alive; contexts open their own connections to it.
    private readonly SqliteConnection _keepAlive;

    public Microsoft.Extensions.Time.Testing.FakeTimeProvider Clock { get; }
    public IDbContextFactory<IdentityDbContext> DbFactory { get; }
    public Argon2PasswordHasher FastHasher { get; }
    public Argon2PasswordHasher RealHasher { get; }
    public PasswordPolicy PasswordPolicy { get; }
    public SigningKeyRing KeyRing { get; }
    public SessionRegistry SessionRegistry { get; }
    public RefreshTokenStore RefreshStore { get; }
    public JwtTokenService TokenService { get; }
    public LockoutService LockoutService { get; }
    public SignInRateLimiter RateLimiter { get; }
    public UserDirectory UserDirectory { get; }
    public FakeSecretVault Vault { get; } = new();
    public RecordingAuditSink Audit { get; } = new();
    public RecordingSystemEventSink Events { get; } = new();
    public IdentityAuditor Auditor { get; }
    public WebAuthnService WebAuthn { get; }
    public MfaService Mfa { get; }
    public FakePermissionProvider Permissions { get; } = new();
    public ClientBindingOptions ClientBinding { get; set; } = new();
    public List<ISessionAdmission> Admissions { get; } = [];

    public TestIdentityHarness(
        Microsoft.Extensions.Time.Testing.FakeTimeProvider? clock = null,
        PasswordPolicyOptions? policyOptions = null,
        MfaOptions? mfaOptions = null,
        WebAuthnOptions? webAuthnOptions = null,
        LockoutOptions? lockoutOptions = null)
    {
        Clock = clock ?? TestTime.CreateProvider();
        // One connection per context, to a named shared-cache in-memory database: SessionRegistry.Touch persists on a
        // background task, and two contexts sharing one SqliteConnection fail with "unable to delete/modify
        // user-function due to active statements" when that write overlaps the test's own query.
        var connectionString = $"Data Source=file:identity-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseSqlite(connectionString)
            .Options;

        var info = new StorageContextInfo("Identity", StorageNaming.TablePrefixes, Clock);
        DbFactory = new SimpleDbContextFactory(() => new IdentityDbContext(options, info));

        using (var db = DbFactory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        FastHasher = new Argon2PasswordHasher(Argon2PasswordHasher.TestParameters);
        RealHasher = new Argon2PasswordHasher(Argon2PasswordHasher.DefaultParameters);

        PasswordPolicy = new PasswordPolicy(policyOptions ?? new PasswordPolicyOptions(), DbFactory, FastHasher);
        KeyRing = new SigningKeyRing(Clock);
        SessionRegistry = new SessionRegistry(DbFactory, Clock);
        RefreshStore = new RefreshTokenStore(DbFactory, SessionRegistry, Clock);
        TokenService = new JwtTokenService(KeyRing, SessionRegistry, Clock);
        Auditor = new IdentityAuditor(Clock, Audit, Events);
        LockoutService = new LockoutService(DbFactory, Clock, lockoutOptions, auditor: Auditor);
        RateLimiter = new SignInRateLimiter(Clock);

        UserDirectory = new UserDirectory(DbFactory, SessionRegistry, PasswordPolicy, FastHasher, Clock, LockoutService, policyOptions, Auditor);
        WebAuthn = new WebAuthnService(DbFactory, Clock, webAuthnOptions);
        Mfa = new MfaService(DbFactory, UserDirectory, FastHasher, Clock, WebAuthn, Auditor, mfaOptions, Permissions, Vault);
    }

    public AuthenticationService CreateAuthService(SignInRateLimiter? rateLimiter = null, Argon2PasswordHasher? hasher = null, ILogger<AuthenticationService>? logger = null) =>
        new(DbFactory, UserDirectory, hasher ?? FastHasher, TokenService, RefreshStore, SessionRegistry, LockoutService, rateLimiter ?? RateLimiter, Admissions, Clock,
            Mfa, Auditor, logger: logger, permissions: Permissions, binding: ClientBinding);

    public void Dispose()
    {
        Mfa.Dispose();
        KeyRing.Dispose();
        _keepAlive.Dispose();
    }

    private sealed class SimpleDbContextFactory(Func<IdentityDbContext> factory) : IDbContextFactory<IdentityDbContext>
    {
        public IdentityDbContext CreateDbContext() => factory();
        public Task<IdentityDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(factory());
    }
}
