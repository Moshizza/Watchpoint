using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Configuration.Contracts;

/// <summary>The client kind rule of the configuration endpoints (spec API §5.3): configuration is only available from the desktop client.</summary>
public static class ConfigurationClientRule
{
    /// <summary>Checks the client kind of the token.</summary>
    /// <param name="clientKind">The client kind (<c>ck</c> claim).</param>
    /// <returns>Success for the desktop client, else <c>api.client_kind.not_allowed</c> (argument <c>required</c>: <c>desktop</c>).</returns>
    public static Result Check(ClientKind clientKind) =>
        clientKind == ClientKind.Desktop ? Result.Success() : ConfigurationErrors.ClientKindNotAllowed("desktop");
}
