using Watchpoint.Commands.Contracts;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Commands.Internal;

/// <summary>
/// The M2 authorizer used until RGT registers its own (spec CMD §5): administrators may run every command and raise to
/// critical priority; other users (and identities of automations, scripts…) may run normal and sensitive commands;
/// nobody needs approval; unknown or disabled identities may run nothing. The reason for critical commands is required
/// by the validation itself.
/// </summary>
internal sealed class FallbackCommandAuthorizer(IUserDirectory users) : ICommandAuthorizer
{
    public CommandAuthorization Authorize(CommandIssuer issuer, Device device, CommandDefinition command, Risk effectiveRisk,
        IReadOnlyDictionary<string, WpValue> arguments, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(command);
        var permission = Permission(command);
        if (!users.Get(issuer.Identity).TryGetValue(out var user, out _) || !user.Enabled)
        {
            return CommandAuthorization.Deny(CommonErrors.Forbidden(permission));
        }

        if (user.Administrator)
        {
            return CommandAuthorization.Allow(new CommandConstraints(MayRaiseToCritical: true));
        }

        return effectiveRisk is Risk.Normal or Risk.Sensitive
            ? CommandAuthorization.Allow(CommandConstraints.None)
            : CommandAuthorization.Deny(CommonErrors.Forbidden(permission));
    }

    public bool CanApprove(UserId approver, Command command, DateTimeOffset now) =>
        users.Get(approver).TryGetValue(out var user, out _) && user.Enabled && user.Administrator;

    /// <summary>The permission of a command (RGT §6.1): <c>commands.&lt;capability&gt;.&lt;command&gt;</c>.</summary>
    public static PermissionCode Permission(CommandDefinition command) => PermissionCode.FromTrusted("commands." + command.Path.Value);
}
