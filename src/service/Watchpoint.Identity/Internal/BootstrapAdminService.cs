using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal.Storage;

namespace Watchpoint.Identity.Internal;

/// <summary>Implementation of <see cref="IBootstrapAdminService"/>.</summary>
internal sealed class BootstrapAdminService : IBootstrapAdminService
{
    private readonly IDbContextFactory<IdentityDbContext> _dbFactory;
    private readonly IUserDirectory _userDirectory;
    private readonly BootstrapAdminOptions _options;
    private readonly ILogger<BootstrapAdminService> _logger;

    public BootstrapAdminService(
        IDbContextFactory<IdentityDbContext> dbFactory,
        IUserDirectory userDirectory,
        BootstrapAdminOptions? options = null,
        ILogger<BootstrapAdminService>? logger = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _userDirectory = userDirectory ?? throw new ArgumentNullException(nameof(userDirectory));
        _options = options ?? new BootstrapAdminOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BootstrapAdminService>.Instance;
    }

    public async Task ExecuteAsync(bool isProduction, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Check if a Person administrator already exists
        var adminExists = await db.Users.AnyAsync(
            u => u.Administrator && u.Kind == UserKind.Person.ToString() && u.DeletedAt == null,
            ct).ConfigureAwait(false);

        if (adminExists)
        {
            return;
        }

        // Look up password from environment variable
        var password = Environment.GetEnvironmentVariable(_options.PasswordEnv);
        if (string.IsNullOrWhiteSpace(password))
        {
            // If absent in Production, log Critical 2701
            IdentityLog.NoAdministrator(_logger);
            return;
        }

        var newAdmin = new NewUser(
            Login: _options.Login,
            DisplayName: "Administrator",
            Administrator: true,
            MustChangePassword: isProduction);

        await _userDirectory.CreateAsync(newAdmin, password, new ChangeContext(null, "bootstrap"), ct).ConfigureAwait(false);
    }
}
