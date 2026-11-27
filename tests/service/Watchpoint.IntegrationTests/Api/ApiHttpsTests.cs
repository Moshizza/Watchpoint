using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Shouldly;
using Watchpoint.IntegrationTests.Host;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.Api;

/// <summary>The main endpoint over HTTPS with the site certificate (spec API §11, SEC §7).</summary>
[Trait("layer", "I")]
[Trait("spec", "API")]
public sealed class ApiHttpsTests(PostgresContainerFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task API_I_011_HTTPS_with_the_site_certificate()
    {
        await using var db = await postgres.CreateDatabaseAsync(nameof(ApiHttpsTests), Ct);
        var port = HostProcess.GetFreePort();
        await using var host = await HostProcess.StartWithNatsAsync("https", new Dictionary<string, JsonNode>
        {
            ["Database"] = HostProcess.DatabaseSection(db.ConnectionString),
            ["Http"] = new JsonObject { ["Urls"] = new JsonArray($"https://127.0.0.1:{port}") },
        }, Ct);

        using var management = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.ManagementPort}/") };
        await ApiNodeFixture.PollAsync(async () =>
        {
            try
            {
                return (await management.GetAsync("health/ready", Ct)).IsSuccessStatusCode;
            }
            catch (HttpRequestException)
            {
                return false;
            }
        }, TestTimeouts.Container * 3, "the host is ready", Ct);

        // First contact: take whatever certificate is presented, then fetch the site CA over that connection.
        X509Certificate2? presented = null;
        using var trustAll = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                presented = certificate is null ? null : X509CertificateLoader.LoadCertificate(certificate.RawData);
                return true;
            },
        };
        using var first = new HttpClient(trustAll) { BaseAddress = new Uri($"https://127.0.0.1:{port}/") };
        var ca = JsonNode.Parse(await first.GetStringAsync("api/v1/system/ca", Ct))!;
        using var root = X509Certificate2.CreateFromPem(ca["pem"]!.GetValue<string>());
        root.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256).ShouldBe(ca["fingerprintSha256"]!.GetValue<string>(), StringCompareShould.IgnoreCase);

        // The presented certificate chains to the site CA.
        presented.ShouldNotBeNull();
        using (presented)
        using (var chain = new X509Chain())
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.Build(presented).ShouldBeTrue(string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
        }

        // A client trusting only the site CA talks to the node.
        using var pinned = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
                {
                    return false;
                }

                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(certificate);
            },
        };
        using var client = new HttpClient(pinned) { BaseAddress = new Uri($"https://127.0.0.1:{port}/") };
        JsonNode.Parse(await client.GetStringAsync("api/v1/system/info", Ct))!["product"]!.GetValue<string>().ShouldBe("Watchpoint");
    }
}
