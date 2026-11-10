using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;

namespace Watchpoint.Integration.Mqtt;

public sealed record MqttSettings
{
    public required string Host { get; init; }
    public int Port { get; init; } = 1883;
    public string ProtocolVersion { get; init; } = "5.0";
    public bool AllowPlain { get; init; }
    public MqttTlsSettings Tls { get; init; } = new();
    public string? ClientId { get; init; }
    public bool CleanSession { get; init; }
    public string KeepAlive { get; init; } = "PT30S";
    public MqttAuthSettings Auth { get; init; } = new();
    public MqttAvailabilitySettings? Availability { get; init; }
    public int MaxMessagesPerSecond { get; init; } = 5000;
    public TimeSpan? StaleAfter { get; init; }
    public TimeSpan? DeviceOfflineAfter { get; init; }
    public int MaxDevices { get; init; } = 10000;
}

public sealed record MqttTlsSettings
{
    public bool Enabled { get; init; }
    public string Mode { get; init; } = "system";
    public string? CustomCaCertificate { get; init; }
    public string? PinnedCertificateThumbprint { get; init; }
}

public sealed record MqttAuthSettings
{
    public string? Username { get; init; }
    public bool ClientCertificate { get; init; }
}

public sealed record MqttAvailabilitySettings
{
    public required string Topic { get; init; }
    public required string OnlinePayload { get; init; }
    public required string OfflinePayload { get; init; }
}

public sealed class MqttSession
{
    private readonly IMqttClient _client;

    public IMqttClient Client => _client;

    public MqttSession(IMqttClient? customClient = null)
    {
        _client = customClient ?? new MqttFactory().CreateMqttClient();
    }

    public static MqttClientOptions BuildClientOptions(
        MqttSettings settings,
        string clientId,
        string? password = null,
        X509Certificate2? clientCert = null,
        string? clientKey = null)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port)
            .WithClientId(clientId)
            .WithCleanSession(settings.CleanSession);

        if (settings.ProtocolVersion == "3.1.1")
        {
            builder.WithProtocolVersion(MqttProtocolVersion.V311);
        }
        else if (settings.ProtocolVersion == "3.1")
        {
            builder.WithProtocolVersion(MqttProtocolVersion.V310);
        }
        else
        {
            builder.WithProtocolVersion(MqttProtocolVersion.V500);
        }

        if (settings.KeepAlive.StartsWith("PT", StringComparison.Ordinal) && int.TryParse(settings.KeepAlive[2..^1], CultureInfo.InvariantCulture, out var sec))
        {
            builder.WithKeepAlivePeriod(TimeSpan.FromSeconds(sec));
        }

        if (!string.IsNullOrEmpty(settings.Auth.Username))
        {
            builder.WithCredentials(settings.Auth.Username, password);
        }

        if (settings.Tls.Enabled)
        {
            builder.WithTlsOptions(tls =>
            {
                tls.UseTls();

                if (settings.Tls.Mode == "pin" && !string.IsNullOrEmpty(settings.Tls.PinnedCertificateThumbprint))
                {
                    tls.WithCertificateValidationHandler(args =>
                    {
                        var thumbprint = args.Certificate?.GetCertHashString();
                        return string.Equals(thumbprint, settings.Tls.PinnedCertificateThumbprint, StringComparison.OrdinalIgnoreCase);
                    });
                }
                else if (settings.Tls.Mode == "customCa" && !string.IsNullOrEmpty(settings.Tls.CustomCaCertificate))
                {
                    var ca = X509Certificate2.CreateFromPem(settings.Tls.CustomCaCertificate);
                    tls.WithCertificateValidationHandler(args =>
                    {
                        using var chain = new X509Chain();
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.Add(ca);
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        return chain.Build(new X509Certificate2(args.Certificate));
                    });
                }

                if (clientCert != null)
                {
                    tls.WithClientCertificates([clientCert]);
                }
            });
        }

        return builder.Build();
    }
}
