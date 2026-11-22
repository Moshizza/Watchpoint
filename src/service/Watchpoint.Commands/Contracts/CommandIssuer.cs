using System.Text.Json.Serialization;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Commands.Contracts;

/// <summary>Who issued a command (spec CMD §4). Every issuer acts under a user identity checked by the authorizer.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(User), "user")]
[JsonDerivedType(typeof(Automation), "automation")]
[JsonDerivedType(typeof(Script), "script")]
[JsonDerivedType(typeof(AlarmAction), "alarmAction")]
[JsonDerivedType(typeof(Federation), "federation")]
public abstract record CommandIssuer
{
    private CommandIssuer()
    {
    }

    /// <summary>The user identity the command runs under (the user, or the identity of the automation, script…).</summary>
    [JsonIgnore]
    public abstract UserId Identity { get; }

    /// <summary>An interactive user.</summary>
    /// <param name="UserId">The user.</param>
    /// <param name="SessionId">The session the request came from (step-up tokens are bound to it).</param>
    /// <param name="ClientKind">The client the user works with.</param>
    /// <param name="WorkstationId">The workstation, if known.</param>
    public sealed record User(UserId UserId, SessionId SessionId, ClientKind ClientKind, string? WorkstationId) : CommandIssuer
    {
        /// <inheritdoc />
        public override UserId Identity => UserId;
    }

    /// <summary>An automation run.</summary>
    /// <param name="AutomationId">The automation.</param>
    /// <param name="RunId">The run.</param>
    /// <param name="IdentityId">The identity the automation runs under.</param>
    /// <param name="LifeSafety">Whether the automation is flagged <c>lifeSafety</c> (fire, evacuation): its commands may run at <see cref="CommandPriority.Critical"/>.</param>
    public sealed record Automation(AutomationId AutomationId, AutomationRunId RunId, UserId IdentityId, bool LifeSafety = false) : CommandIssuer
    {
        /// <inheritdoc />
        public override UserId Identity => IdentityId;
    }

    /// <summary>A script.</summary>
    /// <param name="ScriptId">The script.</param>
    /// <param name="IdentityId">The identity the script runs under.</param>
    public sealed record Script(ScriptId ScriptId, UserId IdentityId) : CommandIssuer
    {
        /// <inheritdoc />
        public override UserId Identity => IdentityId;
    }

    /// <summary>An alarm action.</summary>
    /// <param name="AlarmId">The alarm.</param>
    /// <param name="RuleId">The alarm rule.</param>
    /// <param name="IdentityId">The identity the action runs under.</param>
    public sealed record AlarmAction(AlarmId AlarmId, AlarmRuleId RuleId, UserId IdentityId) : CommandIssuer
    {
        /// <inheritdoc />
        public override UserId Identity => IdentityId;
    }

    /// <summary>A user of a federated site.</summary>
    /// <param name="LinkId">The federation link.</param>
    /// <param name="RemoteUser">The remote user's name.</param>
    /// <param name="IdentityId">The local identity of the link.</param>
    public sealed record Federation(FederationLinkId LinkId, string RemoteUser, UserId IdentityId) : CommandIssuer
    {
        /// <inheritdoc />
        public override UserId Identity => IdentityId;
    }
}
