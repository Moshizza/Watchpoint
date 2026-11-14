using Watchpoint.Hosting;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Host.Dev;

/// <summary>Creates the users of the development seed through <see cref="IUserDirectory"/> (spec HST §9, IDN §7.1).</summary>
public sealed class UsersDevSeedStep(IUserDirectory users, NodeSettings settings) : IDevSeedStep
{
    /// <inheritdoc />
    public string Section => "users";

    /// <inheritdoc />
    public Task<bool> HasDataAsync(DevSeedDocument seed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seed);
        return Task.FromResult(seed.Users.Any(u => users.FindByLogin(u.Login).IsSuccess));
    }

    /// <inheritdoc />
    public async Task<Result> ApplyAsync(DevSeedDocument seed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (settings.Node.Environment != NodeEnvironment.Development)
        {
            // The start-up task already refuses; a step must not create seed accounts either, whoever calls it.
            return Error.Forbidden(DevSeedReader.NotDevelopmentCode);
        }

        var context = new ChangeContext(Actor: null, Origin: "seed");
        foreach (var user in seed.Users)
        {
            var created = await users
                .CreateAsync(new NewUser(user.Login, user.DisplayName, Administrator: user.Administrator), user.Password, context, ct)
                .ConfigureAwait(false);
            if (!created.IsSuccess)
            {
                return created.Error;
            }
        }

        return Result.Success();
    }
}
