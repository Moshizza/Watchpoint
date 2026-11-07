using System.Reflection;
using System.Text;
using System.Text.Json;
using Shouldly;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Kernel.Json;
using Xunit;

namespace Watchpoint.Integration.Abstractions.Tests;

public sealed class ContextTests
{
    [Fact]
    public void INT_U_005_ConnectorSecret_hides_value_and_zeroes_on_dispose()
    {
        var secret = new ConnectorSecret("s3cr3t"u8);

        secret.ToString().ShouldBe("***");
        $"{secret}".ShouldNotContain("s3cr3t");
        secret.UseAsString(s => s).ShouldBe("s3cr3t");
        secret.UseBytes(b => Encoding.UTF8.GetString(b)).ShouldBe("s3cr3t");
        secret.IsZeroed.ShouldBeFalse();

        secret.Dispose();

        secret.IsZeroed.ShouldBeTrue();
        Should.Throw<ObjectDisposedException>(() => secret.UseAsString(s => s));
        Should.Throw<ObjectDisposedException>(() => secret.UseBytes(b => b.Length));
        secret.Dispose();
    }

    [Fact]
    public void INT_U_005_ConnectorSecret_copies_its_input()
    {
        var input = "abc"u8.ToArray();
        using var secret = new ConnectorSecret(input);
        input[0] = (byte)'x';
        secret.UseAsString(s => s).ShouldBe("abc");
    }

    [Fact]
    public void INT_U_006_HttpClientOptions_cannot_express_ignoring_TLS_errors()
    {
        string[] forbidden = ["ignore", "skip", "dangerous", "accept", "callback", "validat", "insecure", "trustall", "bypass"];
        foreach (var type in new[] { typeof(HttpClientOptions), typeof(ISecureHttp) })
        {
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(m => m.Name.ToLowerInvariant())
                .ToList();
            members.ShouldNotBeEmpty();
            foreach (var word in forbidden)
            {
                members.ShouldNotContain(m => m.Contains(word, StringComparison.Ordinal), type.Name + " has a member containing " + word);
            }
        }

        // Every option is plain data: no delegate can smuggle a certificate validation callback in.
        typeof(HttpClientOptions).GetProperties()
            .ShouldAllBe(p => !typeof(Delegate).IsAssignableFrom(p.PropertyType) && p.PropertyType != typeof(HttpMessageHandler));
        typeof(HttpClientOptions).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal)
            .ShouldBe(["AllowHttp", "BaseAddress", "CustomCaPem", "PinnedPublicKeySha256", "Timeout"]);
    }

    [Fact]
    public void INT_U_007_InboundAuth_variants_serialise_with_a_kind()
    {
        var options = WatchpointJson.Create(JsonProfile.Api, IntegrationJson.Resolver);
        InboundAuth[] all =
        [
            new InboundAuth.SharedSecretHeader("X-Token", "webhookToken"),
            new InboundAuth.HmacSignature("X-Signature", "webhookKey", "HMACSHA256"),
            new InboundAuth.ClientCertificate("ab12"),
            new InboundAuth.SourceAddress(["10.0.0.0/8", "192.168.1.0/24"]),
        ];

        foreach (var auth in all)
        {
            var json = JsonSerializer.Serialize(auth, options);
            var back = JsonSerializer.Deserialize<InboundAuth>(json, options)!;
            back.GetType().ShouldBe(auth.GetType());
            JsonSerializer.Serialize(back, options).ShouldBe(json);
        }

        JsonSerializer.Serialize(all[0], options).ShouldBe("""{"kind":"sharedSecretHeader","headerName":"X-Token","secretFieldId":"webhookToken"}""");
        JsonSerializer.Serialize(all[3], options).ShouldBe("""{"kind":"sourceAddress","allowedCidrs":["10.0.0.0/8","192.168.1.0/24"]}""");
        var parsed = JsonSerializer.Deserialize<InboundAuth>("""{"kind":"hmacSignature","headerName":"X-Sig","secretFieldId":"k","algorithm":"HMACSHA256"}""", options);
        parsed.ShouldBe(new InboundAuth.HmacSignature("X-Sig", "k", "HMACSHA256"));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<InboundAuth>("""{"kind":"none"}""", options));
    }
}
