using Xunit;

namespace Watchpoint.IntegrationTests.Host;

/// <summary>
/// The test classes that start <c>Watchpoint.Host</c> processes run one after the other: besides its configured URLs a
/// host also binds the default ones (<c>Http:Urls</c> 8443, <c>Management:Urls</c> 9464 — the settings binder appends to
/// the default lists), so two hosts at the same time compete for those ports.
/// </summary>
[CollectionDefinition(Name)]
public sealed class HostProcesses
{
    public const string Name = "Host processes";
}
