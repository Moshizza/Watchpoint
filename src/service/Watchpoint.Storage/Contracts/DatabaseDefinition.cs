namespace Watchpoint.Storage.Contracts;

/// <summary>TLS mode of a database connection.</summary>
public enum TlsMode
{
    /// <summary>No TLS (allowed only for local hosts unless <c>Database:AllowInsecureRemote</c>).</summary>
    Disable,

    /// <summary>TLS when the server offers it.</summary>
    Prefer,

    /// <summary>TLS required, certificate not verified.</summary>
    Require,

    /// <summary>TLS required, certificate chain verified.</summary>
    VerifyCa,

    /// <summary>TLS required, certificate chain and host name verified.</summary>
    VerifyFull,
}

/// <summary>A database a store can be mapped to (spec STO §5, settings §8).</summary>
/// <param name="Key">Settings key, e.g. <c>default</c>.</param>
/// <param name="ProviderId">Provider id, e.g. <c>postgres</c>.</param>
/// <param name="Host">Host name, IP address or Unix socket directory.</param>
/// <param name="Port">Port.</param>
/// <param name="Database">Database name.</param>
/// <param name="Username">User name.</param>
/// <param name="PasswordRef">Reference of the password secret (SEC); resolved by <see cref="IDatabasePasswordProvider"/>.</param>
/// <param name="Tls">TLS mode.</param>
/// <param name="ExtraOptions">Provider-specific connection options (<c>Key=Value;…</c>), applied last.</param>
/// <param name="AdditionalHosts">Failover hosts (<c>host:port</c>).</param>
/// <param name="MaxPoolSize">Maximum pooled connections.</param>
public sealed record DatabaseDefinition(
    string Key,
    string ProviderId,
    string Host,
    int Port,
    string Database,
    string Username,
    string? PasswordRef,
    TlsMode Tls,
    string? ExtraOptions,
    IReadOnlyList<string> AdditionalHosts,
    int MaxPoolSize)
{
    /// <summary>Gets the clear-text password allowed in Development only (<c>PasswordDev</c>); never logged.</summary>
    public string? PasswordDev { get; init; }

    /// <summary>Returns a description safe for logs: no password.</summary>
    /// <returns><c>provider://user@host:port/database</c>.</returns>
    public override string ToString() => $"{ProviderId}://{Username}@{Host}:{Port}/{Database}";
}

/// <summary>What a probe found on the server.</summary>
/// <param name="ServerVersion">Server version, e.g. <c>17.2</c>.</param>
/// <param name="Edition">Edition, when the server has editions.</param>
/// <param name="Extensions">Installed extensions.</param>
/// <param name="SizeBytes">Database size.</param>
public sealed record DatabaseInfo(string ServerVersion, string? Edition, IReadOnlyList<string> Extensions, long SizeBytes);
