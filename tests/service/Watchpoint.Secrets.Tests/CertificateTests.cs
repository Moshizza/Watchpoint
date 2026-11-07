using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Shouldly;
using Xunit;

namespace Watchpoint.Secrets.Tests;

[Trait("layer", "U")]
[Trait("spec", "SEC")]
public sealed class CertificateTests : IDisposable
{
    private readonly SecretsHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void SEC_U_018_CA_and_node_certificate_generation()
    {
        var service = _harness.CreateCertificates();

        var ca = service.GetCaCertificate();
        var node = service.GetServerCertificate();

        var constraints = ca.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        (constraints.CertificateAuthority, constraints.HasPathLengthConstraint, constraints.PathLengthConstraint).ShouldBe((true, true, 0));
        ca.Subject.ShouldBe("CN=Watchpoint Site CA Test site");
        ca.GetECDsaPublicKey()!.ExportParameters(false).Curve.Oid.Value.ShouldBe("1.2.840.10045.3.1.7"); // P-256
        (ca.NotAfter - ca.NotBefore).TotalDays.ShouldBeGreaterThan(3649);
        File.Exists(Path.Combine(_harness.Options.CertificatesDirectory, "site-ca.crt")).ShouldBeTrue();
        _harness.Bootstrap.Exists("site-ca.key").ShouldBeTrue();

        node.HasPrivateKey.ShouldBeTrue();
        var san = node.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        san.EnumerateDnsNames().ShouldContain("localhost");
        san.EnumerateIPAddresses().ShouldContain(IPAddress.Loopback);
        node.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value).ShouldContain("1.3.6.1.5.5.7.3.1");
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = _harness.Clock.GetUtcNow().UtcDateTime;
        chain.Build(node).ShouldBeTrue(string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation)));
        service.GetCaFingerprintSha256().ShouldBe(ca.GetCertHashString(HashAlgorithmName.SHA256));

        // A second service on the same data folder loads the same certificates.
        var reloaded = _harness.CreateCertificates();
        reloaded.GetCaCertificate().Thumbprint.ShouldBe(ca.Thumbprint);
        reloaded.GetServerCertificate().Thumbprint.ShouldBe(node.Thumbprint);
        reloaded.GetServerCertificate().HasPrivateKey.ShouldBeTrue();
    }

    [Fact]
    public void SEC_U_019_Renewal_at_less_than_30_days()
    {
        var service = _harness.CreateCertificates();
        var first = service.GetServerCertificate();
        service.CheckRenewal();
        service.GetServerCertificate().Thumbprint.ShouldBe(first.Thumbprint);

        _harness.Clock.AdvanceTo(new DateTimeOffset(first.NotAfter.ToUniversalTime()).AddDays(-29));
        service.CheckRenewal();

        var renewed = service.GetServerCertificate();
        renewed.Thumbprint.ShouldNotBe(first.Thumbprint);
        renewed.NotAfter.ShouldBeGreaterThan(first.NotAfter);
        _harness.Logs.WithEventId(2240).Count().ShouldBe(2); // first issue + renewal
    }

    [Fact]
    public void SEC_U_020_Customer_certificate_overrides()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var customer = new CertificateRequest("CN=watchpoint.customer.example", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(_harness.Clock.GetUtcNow().AddDays(-1), _harness.Clock.GetUtcNow().AddYears(1));
        Directory.CreateDirectory(_harness.Options.DataDirectory);
        var path = Path.Combine(_harness.Options.DataDirectory, "customer.pfx");
        File.WriteAllBytes(path, customer.Export(X509ContentType.Pkcs12));
        _harness.Options.CertificatePath = path;

        var service = _harness.CreateCertificates();

        service.GetServerCertificate().Thumbprint.ShouldBe(customer.Thumbprint);
        service.GetServerCertificate().HasPrivateKey.ShouldBeTrue();
        service.GetCaCertificate().Subject.ShouldStartWith("CN=Watchpoint Site CA");
        File.Exists(Path.Combine(_harness.Options.CertificatesDirectory, "node.crt")).ShouldBeFalse();
    }
}
