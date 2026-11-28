using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Shouldly;
using Watchpoint.IntegrationTests.Api;
using Watchpoint.Testing.Async;
using Xunit;

namespace Watchpoint.IntegrationTests.Audit;

/// <summary>The audit endpoints on a real host (spec AUD §9, T3.06).</summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
[Trait("layer", "I")]
[Trait("spec", "AUD")]
public sealed class AuditApiTests(ApiNodeFixture node) : IClassFixture<ApiNodeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Range(int days = 1) =>
        "from=" + Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-days).ToString("O", CultureInfo.InvariantCulture)) +
        "&to=" + Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O", CultureInfo.InvariantCulture));

    [Fact]
    public async Task AUD_I_005_Audit_API_requires_the_permission_and_serves_query_detail_export_and_verification()
    {
        // No token: authentication is required.
        using (var anonymous = node.Client())
        {
            (await anonymous.GetAsync("api/v1/audit?" + Range(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await anonymous.GetAsync("api/v1/audit/export?" + Range(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var admin = node.Client(node.AdminToken);

        // The administrator's own sign-in was recorded by the identity module through the audit trail.
        var signedIn = await FindAsync(admin, "api/v1/audit?action=identity.signedIn&" + Range());
        signedIn["action"]!.GetValue<string>().ShouldBe("identity.signedIn");
        signedIn["outcome"]!.GetValue<string>().ShouldBe("success");
        signedIn["chainId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();

        // Detail by id; unknown id and bad parameters give problems.
        var detail = JsonNode.Parse(await admin.GetStringAsync("api/v1/audit/" + signedIn["id"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture), Ct))!;
        detail["sequence"]!.GetValue<long>().ShouldBe(signedIn["sequence"]!.GetValue<long>());
        (await admin.GetAsync("api/v1/audit/999999999", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetAsync("api/v1/audit", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var tooLarge = await admin.GetAsync("api/v1/audit?" + Range(400), Ct);
        tooLarge.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonNode.Parse(await tooLarge.Content.ReadAsStringAsync(Ct))!["code"]!.GetValue<string>().ShouldBe("audit.query.range_too_large");

        // Export streams CSV and is itself audited.
        using var export = await admin.GetAsync("api/v1/audit/export?format=csv&" + Range(), Ct);
        export.StatusCode.ShouldBe(HttpStatusCode.OK);
        export.Content.Headers.ContentType?.MediaType.ShouldBe("text/csv");
        var csv = await export.Content.ReadAsStringAsync(Ct);
        csv.ShouldStartWith("occurredAt,recordedAt,chain,sequence,action,actionLabel");
        csv.ShouldContain("identity.signedIn");
        var exported = await FindAsync(admin, "api/v1/audit?action=audit.exported&" + Range());
        exported["severity"]!.GetValue<string>().ShouldBe("sensitive");

        // On-demand verification runs as a job and the chain is intact.
        using var started = await admin.PostAsync("api/v1/audit/verification", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), Ct);
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var jobId = JsonNode.Parse(await started.Content.ReadAsStringAsync(Ct))!["jobId"]!.GetValue<string>();
        JsonNode finished = null!;
        await ApiNodeFixture.PollAsync(
            async () =>
            {
                finished = JsonNode.Parse(await admin.GetStringAsync("api/v1/jobs/" + jobId, Ct))!;
                return finished["status"]!.GetValue<string>() != "running";
            },
            TestTimeouts.Container,
            "the verification job finishes",
            Ct);
        finished["status"]!.GetValue<string>().ShouldBe("succeeded");
        finished["result"]![0]!["valid"]!.GetValue<bool>().ShouldBeTrue();
        JsonNode.Parse(await admin.GetStringAsync("api/v1/audit/verification", Ct))!["items"]!.AsArray().ShouldNotBeEmpty();
    }

    /// <summary>Polls a list endpoint until it returns an item (the writer stores records in batches).</summary>
    private static async Task<JsonNode> FindAsync(HttpClient client, string url)
    {
        JsonNode? found = null;
        await ApiNodeFixture.PollAsync(
            async () =>
            {
                found = JsonNode.Parse(await client.GetStringAsync(url, Ct))!["items"]!.AsArray().FirstOrDefault();
                return found is not null;
            },
            TestTimeouts.Container,
            "the audit entry is stored: " + url,
            Ct);
        return found!;
    }
}
