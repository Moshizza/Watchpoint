using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.Integration.Mqtt.Tests;

public sealed class MosquittoFixture : IAsyncLifetime
{
    public const string MosquittoImage = "eclipse-mosquitto:2.0.20@sha256:21421af7b32bf9ce508e9090c8eb13bb81f410ca778dc205506180a6f862d0eb";

    private IContainer? _container;
    private string? _tempDir;
    private X509Certificate2? _caCert;
    private X509Certificate2? _serverCert;
    private X509Certificate2? _clientCert;

    public int PlainPort => _container?.GetMappedPublicPort(1883) ?? 0;
    public int TlsPort => _container?.GetMappedPublicPort(8883) ?? 0;
    public string Host => _container?.Hostname ?? "127.0.0.1";
    public X509Certificate2 CaCertificate => _caCert!;
    public X509Certificate2 ClientCertificate => _clientCert!;

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable())
        {
            return;
        }

        _tempDir = Directory.CreateTempSubdirectory("mosquitto-tls-").FullName;
        GenerateCertificates(_tempDir);

        var config = $"""
            listener 1883 0.0.0.0
            allow_anonymous true

            listener 8883 0.0.0.0
            cafile /mosquitto/config/ca.crt
            certfile /mosquitto/config/server.crt
            keyfile /mosquitto/config/server.key
            require_certificate false
            allow_anonymous true
            """;

        var configPath = Path.Combine(_tempDir, "mosquitto.conf");
        File.WriteAllText(configPath, config);

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(Path.Combine(_tempDir, "server.key"), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                File.SetUnixFileMode(Path.Combine(_tempDir, "server.crt"), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                File.SetUnixFileMode(Path.Combine(_tempDir, "ca.crt"), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            catch
            {
            }
        }

        _container = new ContainerBuilder(MosquittoImage)
            .WithPortBinding(1883, true)
            .WithPortBinding(8883, true)
            .WithBindMount(configPath, "/mosquitto/config/mosquitto.conf", AccessMode.ReadOnly)
            .WithBindMount(Path.Combine(_tempDir, "ca.crt"), "/mosquitto/config/ca.crt", AccessMode.ReadOnly)
            .WithBindMount(Path.Combine(_tempDir, "server.crt"), "/mosquitto/config/server.crt", AccessMode.ReadOnly)
            .WithBindMount(Path.Combine(_tempDir, "server.key"), "/mosquitto/config/server.key", AccessMode.ReadOnly)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(1883))
            .Build();

        await _container.StartAsync();
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        if (_container is not null)
        {
            await _container.StopAsync(ct);
            await _container.StartAsync(ct);
        }
    }

    private void GenerateCertificates(string dir)
    {
        var now = DateTimeOffset.UtcNow;
        var caNotBefore = now.AddDays(-1);
        var caNotAfter = now.AddDays(20);
        var certNotAfter = now.AddDays(10);

        using var rsaCa = RSA.Create(2048);
        var caReq = new CertificateRequest("CN=MosquittoTestCA", rsaCa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        _caCert = caReq.CreateSelfSigned(caNotBefore, caNotAfter);

        using var rsaServer = RSA.Create(2048);
        var serverReq = new CertificateRequest("CN=localhost", rsaServer, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        serverReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        serverReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
        sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        serverReq.CertificateExtensions.Add(sanBuilder.Build());

        var serverCertRaw = serverReq.Create(_caCert, caNotBefore, certNotAfter, Guid.NewGuid().ToByteArray());
        _serverCert = serverCertRaw.CopyWithPrivateKey(rsaServer);

        using var rsaClient = RSA.Create(2048);
        var clientReq = new CertificateRequest("CN=client", rsaClient, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        clientReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        var clientCertRaw = clientReq.Create(_caCert, caNotBefore, certNotAfter, Guid.NewGuid().ToByteArray());
        _clientCert = clientCertRaw.CopyWithPrivateKey(rsaClient);

        File.WriteAllText(Path.Combine(dir, "ca.crt"), _caCert.ExportCertificatePem());
        File.WriteAllText(Path.Combine(dir, "server.crt"), _serverCert.ExportCertificatePem());
        File.WriteAllText(Path.Combine(dir, "server.key"), rsaServer.ExportPkcs8PrivateKeyPem());
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        if (_tempDir is not null && Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
            }
        }

        _caCert?.Dispose();
        _serverCert?.Dispose();
        _clientCert?.Dispose();
    }
}
