using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Shouldly;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Sdk.Logging;
using Watchpoint.Integration.Sdk.Net;
using Xunit;

namespace Watchpoint.Integration.Sdk.Tests;

public sealed class HttpAndLoggingTests
{
    private sealed class TestLogger : ILogger
    {
        public List<(LogLevel Level, EventId EventId, string Message)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Logs)
            {
                Logs.Add((logLevel, eventId, formatter(state, exception)));
            }
        }
    }

    [Fact]
    public void INT_U_037_Protocol_trace_logging_only_when_enabled_masks_declared_secret_fields()
    {
        var logger = new TestLogger();

        // 1. Trace disabled: logs nothing
        logger.LogProtocolTrace(
            traceEnabled: false,
            direction: "SENT",
            payload: """{"token":"super_secret","data":"123"}""",
            secretFields: ["token"]);

        logger.Logs.Count.ShouldBe(0);

        // 2. Trace enabled: logs and masks declared secret fields
        logger.LogProtocolTrace(
            traceEnabled: true,
            direction: "SENT",
            payload: """{"token":"super_secret","customSecret":"abc1234","data":"123"}""",
            secretFields: ["customSecret"]);

        logger.Logs.Count.ShouldBe(1);
        var log = logger.Logs[0];
        log.Level.ShouldBe(LogLevel.Trace);
        log.Message.ShouldContain("PROTOCOL SENT:");
        log.Message.ShouldNotContain("super_secret");
        log.Message.ShouldNotContain("abc1234");
        log.Message.ShouldContain("""{"token":"***","customSecret":"***","data":"123"}""");
    }

    [Fact]
    public async Task INT_U_038_Secure_HTTP_pin_validation()
    {
        // Generate a self-signed certificate for loopback
        using var rsa = RSA.Create(2048);
        var certReq = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(30));

        var publicKey = cert.GetPublicKey();
        var validPinHex = Convert.ToHexString(SHA256.HashData(publicKey));
        var invalidPinHex = new string('0', 64);

        // Start local TLS server
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    using (var sslStream = new SslStream(client.GetStream(), false))
                    {
                        try
                        {
                            await sslStream.AuthenticateAsServerAsync(cert);
                            // Read HTTP GET request
                            var buf = new byte[1024];
                            _ = await sslStream.ReadAsync(buf, cts.Token);
                            // Write HTTP 200 response
                            var response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8.ToArray();
                            await sslStream.WriteAsync(response, cts.Token);
                            await sslStream.FlushAsync(cts.Token);
                        }
                        catch
                        {
                            // Client handshake error expected on pin mismatch
                        }
                    }
                }, cts.Token);
            }
        }, cts.Token);

        var baseUri = new Uri($"https://127.0.0.1:{port}/");

        try
        {
            // 1. Matching pin -> success
            var validOptions = new HttpClientOptions(
                baseUri,
                Timeout: TimeSpan.FromSeconds(5),
                PinnedPublicKeySha256: validPinHex);

            using var validClient = SecureHttp.Instance.CreateClient(validOptions);
            var resp = await validClient.GetStringAsync("", TestContext.Current.CancellationToken);
            resp.ShouldBe("OK");

            // 2. Mismatched pin -> HttpRequestException with TLS error
            var invalidOptions = new HttpClientOptions(
                baseUri,
                Timeout: TimeSpan.FromSeconds(5),
                PinnedPublicKeySha256: invalidPinHex);

            using var invalidClient = SecureHttp.Instance.CreateClient(invalidOptions);
            var ex = await Should.ThrowAsync<HttpRequestException>(async () =>
            {
                await invalidClient.GetStringAsync("", TestContext.Current.CancellationToken);
            });
            ex.InnerException.ShouldNotBeNull();
            (ex.InnerException is AuthenticationException || ex.InnerException is SocketException).ShouldBeTrue();
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
        }
    }

    [Fact]
    public async Task INT_U_039_Secure_HTTP_custom_CA()
    {
        // Generate CA certificate
        var now = DateTimeOffset.UtcNow;
        using var caRsa = RSA.Create(2048);
        var caReq = new CertificateRequest("CN=TestRootCA", caRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, 0, critical: true));
        using var caCert = caReq.CreateSelfSigned(now.AddMinutes(-5), now.AddHours(2));

        // Generate Server certificate signed by CA
        using var serverRsa = RSA.Create(2048);
        var serverReq = new CertificateRequest("CN=127.0.0.1", serverRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        serverReq.CertificateExtensions.Add(sanBuilder.Build());

        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        using var serverCert = serverReq.Create(caCert, now.AddMinutes(-5), now.AddHours(1), serial);
        using var serverCertWithKey = serverCert.CopyWithPrivateKey(serverRsa);

        // Another unrelated CA
        using var otherCaRsa = RSA.Create(2048);
        var otherCaReq = new CertificateRequest("CN=OtherRootCA", otherCaRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        otherCaReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, 0, critical: true));
        using var otherCaCert = otherCaReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(30));

        var caPem = caCert.ExportCertificatePem();
        var otherCaPem = otherCaCert.ExportCertificatePem();

        // Start TLS server with serverCertWithKey
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    using (var sslStream = new SslStream(client.GetStream(), false))
                    {
                        try
                        {
                            await sslStream.AuthenticateAsServerAsync(serverCertWithKey);
                            var buf = new byte[1024];
                            _ = await sslStream.ReadAsync(buf, cts.Token);
                            var response = "HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\nCAOK"u8.ToArray();
                            await sslStream.WriteAsync(response, cts.Token);
                            await sslStream.FlushAsync(cts.Token);
                        }
                        catch
                        {
                            // Expected on handshake error
                        }
                    }
                }, cts.Token);
            }
        }, cts.Token);

        var baseUri = new Uri($"https://127.0.0.1:{port}/");

        try
        {
            // 1. Chained to provided CA -> success
            var validOptions = new HttpClientOptions(
                baseUri,
                Timeout: TimeSpan.FromSeconds(5),
                CustomCaPem: caPem);

            using var validClient = SecureHttp.Instance.CreateClient(validOptions);
            var resp = await validClient.GetStringAsync("", TestContext.Current.CancellationToken);
            resp.ShouldBe("CAOK");

            // 2. Chained to other CA -> failure
            var invalidOptions = new HttpClientOptions(
                baseUri,
                Timeout: TimeSpan.FromSeconds(5),
                CustomCaPem: otherCaPem);

            using var invalidClient = SecureHttp.Instance.CreateClient(invalidOptions);
            await Should.ThrowAsync<HttpRequestException>(async () =>
            {
                await invalidClient.GetStringAsync("", TestContext.Current.CancellationToken);
            });
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
        }
    }

    [Fact]
    public void INT_U_040_Secure_HTTP_refuses_plain_HTTP_unless_allowed()
    {
        var httpUri = new Uri("http://127.0.0.1:8080/");

        // Disallowed by default
        var defaultOptions = new HttpClientOptions(httpUri, Timeout: TimeSpan.FromSeconds(5), AllowHttp: false);
        Should.Throw<InvalidOperationException>(() =>
        {
            SecureHttp.Instance.CreateClient(defaultOptions);
        });

        // Allowed explicitly
        var allowedOptions = new HttpClientOptions(httpUri, Timeout: TimeSpan.FromSeconds(5), AllowHttp: true);
        using var client = SecureHttp.Instance.CreateClient(allowedOptions);
        client.ShouldNotBeNull();
        client.BaseAddress.ShouldBe(httpUri);
    }
}
