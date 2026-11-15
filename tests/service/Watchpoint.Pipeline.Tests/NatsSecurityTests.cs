using NATS.Client.Core;
using Shouldly;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal;
using Watchpoint.Pipeline.Internal.Nats;
using Xunit;

namespace Watchpoint.Pipeline.Tests;

/// <summary>The NATS connection of the event log (spec PIP §13).</summary>
[Trait("layer", "U")]
[Trait("spec", "PIP")]
public sealed class NatsSecurityTests
{
    [Theory]
    [InlineData("nats://127.0.0.1:4222", false)]
    [InlineData("nats://localhost:4222", false)]
    [InlineData("nats://[::1]:4222", false)]
    [InlineData("127.0.0.1:4222", false)]
    [InlineData("nats://nats.example.org:4222", true)]
    [InlineData("nats://10.1.2.3:4222", true)]
    [InlineData("nats://127.0.0.1:4222,nats://10.1.2.3:4222", true)]
    [InlineData("tls://nats.example.org:4222", true)]
    [InlineData("not a url", true)]
    public void PIP_U_048_TLS_required_for_non_loopback_NATS(string url, bool tlsRequired)
    {
        var endpoint = new EventLogEndpoint(url, "s3cret-token");

        var options = NatsConnectionProvider.BuildOptions(endpoint, "test", new PipelineOptions());

        // Off loopback the connection is refused unless the server does TLS; on loopback TLS is used when offered.
        options.TlsOpts.Mode.ShouldBe(tlsRequired ? TlsMode.Require : TlsMode.Auto);
        options.AuthOpts.Token.ShouldBe("s3cret-token");
        endpoint.ToString().ShouldNotContain("s3cret-token");

        // Development and tests may opt out explicitly.
        var insecure = NatsConnectionProvider.BuildOptions(endpoint, "test", new PipelineOptions { AllowInsecureRemote = true });
        insecure.TlsOpts.Mode.ShouldBe(TlsMode.Auto);
    }
}
