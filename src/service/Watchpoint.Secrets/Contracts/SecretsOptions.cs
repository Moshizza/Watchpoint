namespace Watchpoint.Secrets.Contracts;

/// <summary>Node facts the secrets module needs; filled by the host from node settings (HST §4).</summary>
public sealed class SecretsOptions
{
    /// <summary>Gets or sets the node data folder (<c>&lt;Data&gt;</c>): <c>secrets/</c>, <c>certs/</c>, <c>nats/</c> live below it.</summary>
    public string DataDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the node id (DPAPI entropy, CA name when no site name).</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Gets or sets the site name (CA subject), when known.</summary>
    public string? SiteName { get; set; }

    /// <summary>Gets or sets <c>Secrets:MasterKeyProvider</c>: <c>dpapi</c> (Windows default), <c>file</c> (default elsewhere), <c>environment</c>.</summary>
    public string? MasterKeyProvider { get; set; }

    /// <summary>Gets or sets <c>Secrets:MaxValueBytes</c>.</summary>
    public int MaxValueBytes { get; set; } = 65536;

    /// <summary>Gets or sets <c>Http:Certificate:ExtraNames</c>: extra DNS names or IP addresses of the node certificate.</summary>
    public IList<string> CertificateExtraNames { get; } = [];

    /// <summary>Gets or sets <c>Http:Certificate:Path</c>: customer certificate (PFX, or PEM with <see cref="CertificateKeyPath"/>).</summary>
    public string? CertificatePath { get; set; }

    /// <summary>Gets or sets <c>Http:Certificate:KeyPath</c>: PEM private key of the customer certificate.</summary>
    public string? CertificateKeyPath { get; set; }

    /// <summary>Gets or sets <c>Http:Certificate:PasswordRef</c>: bootstrap secret holding the PFX password.</summary>
    public string? CertificatePasswordRef { get; set; }

    /// <summary>Gets the folder of the secrets files.</summary>
    public string SecretsDirectory => Path.Join(DataDirectory, "secrets");

    /// <summary>Gets the folder of the certificates.</summary>
    public string CertificatesDirectory => Path.Join(DataDirectory, "certs");
}
