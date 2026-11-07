namespace Watchpoint.Integration.Abstractions.Model;

/// <summary>Result of <see cref="IConnector.TestAsync"/> (INT §4).</summary>
/// <param name="Success">Whether the test succeeded.</param>
/// <param name="Failure">The failure category, when <paramref name="Success"/> is false.</param>
/// <param name="Message">A message for the administrator.</param>
/// <param name="Identity">What the connector learned about the system, when known.</param>
public sealed record ConnectionTestResult(bool Success, FailureCategory? Failure, ConnectorMessage? Message, ServerIdentity? Identity)
{
    /// <summary>Creates a successful result.</summary>
    /// <param name="identity">The system identity, when known.</param>
    /// <returns>The result.</returns>
    public static ConnectionTestResult Succeeded(ServerIdentity? identity = null) => new(true, null, null, identity);

    /// <summary>Creates a failed result.</summary>
    /// <param name="failure">The failure category.</param>
    /// <param name="message">The message for the administrator.</param>
    /// <returns>The result.</returns>
    public static ConnectionTestResult Failed(FailureCategory failure, ConnectorMessage message) =>
        new(false, failure, message ?? throw new ArgumentNullException(nameof(message)), null);
}

/// <summary>Identity of an integrated system as reported by a connection test.</summary>
/// <param name="Model">The model, when known.</param>
/// <param name="Version">The software version, when known.</param>
/// <param name="Serial">The serial number, when known.</param>
/// <param name="Extra">Further vendor facts (name → value).</param>
public sealed record ServerIdentity(string? Model, string? Version, string? Serial, IReadOnlyDictionary<string, string> Extra);
