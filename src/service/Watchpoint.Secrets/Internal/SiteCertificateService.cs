using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Secrets.Internal;

/// <summary>Site CA and node TLS certificate (spec SEC §7). Private keys are bootstrap secrets; certificates are PEM files.</summary>
internal sealed class SiteCertificateService(
    SecretsOptions options,
    IBootstrapSecretStore bootstrap,
    SecretsMetrics metrics,
    TimeProvider clock,
    ILogger<SiteCertificateService> logger) : ISiteCertificateService
{
    public const string CaKeyName = "site-ca.key";
    public const string NodeKeyName = "node-tls.key";
    public static readonly TimeSpan CaValidity = TimeSpan.FromDays(3650);
    public static readonly TimeSpan NodeValidity = TimeSpan.FromDays(730);
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);
    public static readonly TimeSpan CaWarnBefore = TimeSpan.FromDays(365);

    private readonly Lock _lock = new();
    private X509Certificate2? _ca;
    private X509Certificate2? _server;
    private bool _customer;

    public string CaPath => Path.Join(options.CertificatesDirectory, "site-ca.crt");

    public string NodePath => Path.Join(options.CertificatesDirectory, "node.crt");

    public X509Certificate2 GetServerCertificate()
    {
        Initialize();
        return Volatile.Read(ref _server)!;
    }

    public X509Certificate2 GetCaCertificate()
    {
        Initialize();
        return _ca!;
    }

    public string GetCaFingerprintSha256() => GetCaCertificate().GetCertHashString(HashAlgorithmName.SHA256);

    public Task<X509Certificate2> IssueAsync(CertificateRequestSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Initialize();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Task.FromResult(Issue(spec, key));
    }

    /// <summary>Loads or creates the CA and the node certificate (idempotent).</summary>
    public void Initialize()
    {
        lock (_lock)
        {
            if (_server is not null)
            {
                return;
            }

            _ca = LoadOrCreateCa();
            metrics.TrackCertificate("site-ca", () => DaysLeft(_ca));
            if (!string.IsNullOrEmpty(options.CertificatePath))
            {
                _customer = true;
                _server = LoadCustomerCertificate();
            }
            else
            {
                _server = LoadNodeCertificate() ?? IssueNodeCertificate();
            }

            metrics.TrackCertificate("node", () => DaysLeft(Volatile.Read(ref _server)));
            CheckCaExpiry();
        }
    }

    /// <summary>Re-issues the node certificate when it expires in less than 30 days; warns when the CA expires within a year.</summary>
    public void CheckRenewal()
    {
        Initialize();
        lock (_lock)
        {
            if (!_customer && _server!.NotAfter.ToUniversalTime() - clock.GetUtcNow().UtcDateTime < RenewBefore)
            {
                Volatile.Write(ref _server, IssueNodeCertificate());
            }

            CheckCaExpiry();
        }
    }

    private X509Certificate2 LoadOrCreateCa()
    {
        if (File.Exists(CaPath) && bootstrap.Exists(CaKeyName))
        {
            return WithStoredKey(X509CertificateLoader.LoadCertificateFromFile(CaPath), CaKeyName);
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = $"CN=Watchpoint Site CA {(string.IsNullOrWhiteSpace(options.SiteName) ? options.NodeId : options.SiteName)}";
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        var now = clock.GetUtcNow();
        using var created = request.CreateSelfSigned(now.AddMinutes(-5), now + CaValidity);
        Store(created, key, CaPath, CaKeyName);
        var fingerprint = created.GetCertHashString(HashAlgorithmName.SHA256);
        SecretsLog.CaCreated(logger, subject, fingerprint);
        return Portable(created);
    }

    private X509Certificate2? LoadNodeCertificate()
    {
        if (!File.Exists(NodePath) || !bootstrap.Exists(NodeKeyName))
        {
            return null;
        }

        var certificate = WithStoredKey(X509CertificateLoader.LoadCertificateFromFile(NodePath), NodeKeyName);
        return certificate.NotAfter.ToUniversalTime() - clock.GetUtcNow().UtcDateTime < RenewBefore ? null : certificate;
    }

    private X509Certificate2 IssueNodeCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (dns, ips) = NodeNames();
        var certificate = Issue(new CertificateRequestSpec(Dns.GetHostName(), dns, ips, CertificateUsage.Server, NodeValidity), key);
        Store(certificate, key, NodePath, NodeKeyName);
        DateTimeOffset notAfter = certificate.NotAfter.ToUniversalTime();
        SecretsLog.NodeCertificateRenewed(logger, notAfter);
        return certificate;
    }

    private X509Certificate2 Issue(CertificateRequestSpec spec, ECDsa key)
    {
        var ca = _ca!;
        var request = new CertificateRequest(new X500DistinguishedName("CN=" + spec.CommonName.Replace(",", " ", StringComparison.Ordinal)), key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in spec.DnsNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            san.AddDnsName(name);
        }

        foreach (var ip in spec.IpAddresses.Distinct())
        {
            san.AddIpAddress(ip);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var usages = new OidCollection();
        if (spec.Usage is CertificateUsage.Server or CertificateUsage.ServerAndClient)
        {
            usages.Add(new Oid("1.3.6.1.5.5.7.3.1", "serverAuth"));
        }

        if (spec.Usage is CertificateUsage.Client or CertificateUsage.ServerAndClient)
        {
            usages.Add(new Oid("1.3.6.1.5.5.7.3.2", "clientAuth"));
        }

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        var now = clock.GetUtcNow();
        var notAfter = now + spec.Validity;
        if (notAfter > ca.NotAfter.ToUniversalTime())
        {
            notAfter = ca.NotAfter.ToUniversalTime();
        }

        using var signed = request.Create(ca, now.AddMinutes(-5), notAfter, RandomNumberGenerator.GetBytes(16));
        using var withKey = signed.CopyWithPrivateKey(key);
        return Portable(withKey);
    }

    private (List<string> Dns, List<IPAddress> Ips) NodeNames()
    {
        var dns = new List<string> { Dns.GetHostName(), "localhost" };
        var ips = new List<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        try
        {
            ips.AddRange(NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal));
        }
        catch (NetworkInformationException)
        {
            // No interface information: localhost names only.
        }

        foreach (var extra in options.CertificateExtraNames)
        {
            if (IPAddress.TryParse(extra, out var ip))
            {
                ips.Add(ip);
            }
            else
            {
                dns.Add(extra);
            }
        }

        return (dns, ips);
    }

    private X509Certificate2 LoadCustomerCertificate()
    {
        var path = options.CertificatePath!;
        if (!string.IsNullOrEmpty(options.CertificateKeyPath))
        {
            return Portable(X509Certificate2.CreateFromPemFile(path, options.CertificateKeyPath));
        }

        string? password = null;
        if (options.CertificatePasswordRef is { } reference && SecretRef.Parse(reference).TryGetValue(out var secretRef, out _) && secretRef.IsBootstrap
            && bootstrap.Read(secretRef.BootstrapName).TryGetValue(out var value, out _))
        {
            using (value)
            {
                password = value.UseAsString(p => p);
            }
        }

        return X509CertificateLoader.LoadPkcs12FromFile(path, password);
    }

    private X509Certificate2 WithStoredKey(X509Certificate2 certificate, string keyName)
    {
        using (certificate)
        {
            if (!bootstrap.Read(keyName).TryGetValue(out var value, out var error))
            {
                throw new SecretsException(error);
            }

            using (value)
            {
                using var key = ECDsa.Create();
                value.Use(bytes =>
                {
                    key.ImportPkcs8PrivateKey(bytes, out _);
                    return true;
                });
                using var withKey = certificate.CopyWithPrivateKey(key);
                return Portable(withKey);
            }
        }
    }

    private void Store(X509Certificate2 certificate, ECDsa key, string certificatePath, string keyName)
    {
        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            var written = bootstrap.Write(keyName, pkcs8);
            if (!written.IsSuccess)
            {
                throw new SecretsException(written.Error);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        Directory.CreateDirectory(options.CertificatesDirectory);
        var temp = certificatePath + ".tmp";
        File.WriteAllText(temp, certificate.ExportCertificatePem());
        File.Move(temp, certificatePath, overwrite: true);
    }

    /// <summary>A copy whose private key works with SslStream on every OS (re-imported from PKCS#12).</summary>
    private static X509Certificate2 Portable(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), password: null);

    private void CheckCaExpiry()
    {
        if (_ca!.NotAfter.ToUniversalTime() - clock.GetUtcNow().UtcDateTime < CaWarnBefore)
        {
            DateTimeOffset notAfter = _ca.NotAfter.ToUniversalTime();
            SecretsLog.CaExpiresSoon(logger, notAfter);
        }
    }

    private double? DaysLeft(X509Certificate2? certificate) =>
        certificate is null ? null : (certificate.NotAfter.ToUniversalTime() - clock.GetUtcNow().UtcDateTime).TotalDays;
}
