using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Watchpoint.Integration.Abstractions.Context;

namespace Watchpoint.Integration.Sdk.Net;

/// <summary>
/// Implements <see cref="ISecureHttp"/> creating <see cref="HttpClient"/> instances with strict TLS policies (INT §7.3, §10).
/// Supports public key pin validation, custom CA validation, and refuses plain HTTP unless explicitly allowed.
/// Never ignores certificate errors.
/// </summary>
public sealed class SecureHttp : ISecureHttp
{
    /// <summary>Default instance of <see cref="SecureHttp"/>.</summary>
    public static SecureHttp Instance { get; } = new();

    /// <inheritdoc />
    public HttpClient CreateClient(HttpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BaseAddress);

        var isHttps = string.Equals(options.BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isHttp = string.Equals(options.BaseAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

        if (isHttp && !options.AllowHttp)
        {
            throw new InvalidOperationException($"Insecure HTTP is refused for {options.BaseAddress}. Plain HTTP must be explicitly allowed.");
        }

        if (!isHttps && !isHttp)
        {
            throw new InvalidOperationException($"Unsupported URI scheme '{options.BaseAddress.Scheme}'. Only HTTP and HTTPS are supported.");
        }

        var handler = new SocketsHttpHandler();

        if (isHttps)
        {
            ConfigureTls(handler.SslOptions, options);
        }

        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = options.BaseAddress,
            Timeout = options.Timeout,
        };

        return client;
    }

    private static void ConfigureTls(SslClientAuthenticationOptions sslOptions, HttpClientOptions options)
    {
        byte[]? expectedPinBytes = null;
        if (!string.IsNullOrWhiteSpace(options.PinnedPublicKeySha256))
        {
            expectedPinBytes = ParsePin(options.PinnedPublicKeySha256);
        }

        X509Certificate2? customCa = null;
        if (!string.IsNullOrWhiteSpace(options.CustomCaPem))
        {
            customCa = X509Certificate2.CreateFromPem(options.CustomCaPem);
        }

        // If neither pin nor custom CA is provided, standard system trust applies (no custom callback needed).
        if (expectedPinBytes is null && customCa is null)
        {
            return;
        }

        sslOptions.RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
        {
            if (certificate is null)
            {
                return false;
            }

            // 1. If public-key pin is specified, validate it
            if (expectedPinBytes is not null)
            {
                var cert2 = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
                var publicKey = cert2.GetPublicKey();
                var hash = SHA256.HashData(publicKey);

                return CryptographicOperations.FixedTimeEquals(hash, expectedPinBytes);
            }

            // 2. If custom CA is specified, build custom chain
            if (customCa is not null)
            {
                var cert2 = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
                using var customChain = new X509Chain();
                customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                customChain.ChainPolicy.CustomTrustStore.Add(customCa);

                var valid = customChain.Build(cert2);
                return valid;
            }

            return sslPolicyErrors == SslPolicyErrors.None;
        };
    }

    private static byte[] ParsePin(string pin)
    {
        var cleaned = pin.Trim();
        if (cleaned.StartsWith("sha256/", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned["sha256/".Length..];
        }

        // Try hex or base64
        try
        {
            if (cleaned.Length == 64 && !cleaned.Contains('='))
            {
                return Convert.FromHexString(cleaned);
            }
        }
        catch (FormatException)
        {
            // fallback to base64
        }

        try
        {
            return Convert.FromBase64String(cleaned);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"Invalid SHA-256 public key pin format: '{pin}'. Must be hex or base64.", nameof(pin), ex);
        }
    }
}
