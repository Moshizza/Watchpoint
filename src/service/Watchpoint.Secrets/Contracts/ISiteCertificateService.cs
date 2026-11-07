using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Watchpoint.Secrets.Contracts;

/// <summary>The site certificate authority and the node's TLS certificate (spec SEC §7).</summary>
public interface ISiteCertificateService
{
    /// <summary>The current server certificate (with private key), for Kestrel's certificate selector; changes on renewal.</summary>
    /// <returns>The certificate.</returns>
    X509Certificate2 GetServerCertificate();

    /// <summary>The site CA certificate (public part).</summary>
    /// <returns>The certificate.</returns>
    X509Certificate2 GetCaCertificate();

    /// <summary>SHA-256 fingerprint of the CA certificate (uppercase hex), shown in clients' trust prompt.</summary>
    /// <returns>The fingerprint.</returns>
    string GetCaFingerprintSha256();

    /// <summary>Issues a certificate signed by the site CA (mTLS of nodes and hosts).</summary>
    /// <param name="spec">The request.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The certificate with its private key.</returns>
    Task<X509Certificate2> IssueAsync(CertificateRequestSpec spec, CancellationToken ct);
}

/// <summary>A certificate to issue.</summary>
/// <param name="CommonName">Subject common name.</param>
/// <param name="DnsNames">DNS subject alternative names.</param>
/// <param name="IpAddresses">IP subject alternative names.</param>
/// <param name="Usage">Extended key usage.</param>
/// <param name="Validity">Validity from now.</param>
public sealed record CertificateRequestSpec(string CommonName, IReadOnlyList<string> DnsNames, IReadOnlyList<IPAddress> IpAddresses,
    CertificateUsage Usage, TimeSpan Validity);

/// <summary>Extended key usage of an issued certificate.</summary>
public enum CertificateUsage
{
    /// <summary>TLS server.</summary>
    Server,

    /// <summary>TLS client.</summary>
    Client,

    /// <summary>Both.</summary>
    ServerAndClient,
}

/// <summary>Start-up work of the secrets module, run by the host in phase <c>Infrastructure</c> (HST §5.3).</summary>
public interface ISecretsStartup
{
    /// <summary>Loads (or creates) the master key, imports the legacy NATS token, creates or renews the certificates.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task InitializeAsync(CancellationToken ct);
}
