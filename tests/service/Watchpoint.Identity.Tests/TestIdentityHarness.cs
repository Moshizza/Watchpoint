using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal;
using Watchpoint.Identity.Internal.Lockout;
using Watchpoint.Identity.Internal.Passwords;
using Watchpoint.Identity.Internal.Sessions;
using Watchpoint.Identity.Internal.Storage;
using Watchpoint.Identity.Internal.Tokens;
using Watchpoint.Kernel.Testing;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Identity.Tests;

internal sealed class TestIdentityHarness : IDisposable
{
    private readonly SqliteConnection _connection;

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
    public List<ISessionAdmission> Admissions { get; } = [];

    public TestIdentityHarness(Microsoft.Extensions.Time.Testing.FakeTimeProvider? clock = null, PasswordPolicyOptions? policyOptions = null)
    {
        Clock = clock ?? TestTime.CreateProvider();
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseSqlite(_connection)
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
        LockoutService = new LockoutService(DbFactory, Clock);
        RateLimiter = new SignInRateLimiter(Clock);

        UserDirectory = new UserDirectory(DbFactory, SessionRegistry, PasswordPolicy, FastHasher, Clock);
    }

    public AuthenticationService CreateAuthService() =>
        new(DbFactory, UserDirectory, FastHasher, TokenService, RefreshStore, SessionRegistry, LockoutService, RateLimiter, Admissions, Clock);

    public void Dispose()
    {
        KeyRing.Dispose();
        _connection.Dispose();
    }

    private sealed class SimpleDbContextFactory(Func<IdentityDbContext> factory) : IDbContextFactory<IdentityDbContext>
    {
        public IdentityDbContext CreateDbContext() => factory();
        public Task<IdentityDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(factory());
    }
}
