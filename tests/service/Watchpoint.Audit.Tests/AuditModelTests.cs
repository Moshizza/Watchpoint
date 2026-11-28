using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Watchpoint.Audit.Contracts;
using Watchpoint.Audit.Internal;
using Watchpoint.Identity.Contracts;
using Watchpoint.Secrets.Contracts;
using Watchpoint.Storage.Contracts;
using Xunit;

namespace Watchpoint.Audit.Tests;

[Trait("layer", "U")]
[Trait("spec", "AUD")]
public sealed class AuditModelTests
{
    private static readonly string[] _catalogue =
    [
        "identity.signedIn", "identity.signInFailed", "identity.signedOut", "identity.sessionRevoked", "identity.accountLocked", "identity.accountUnlocked",
        "identity.passwordChanged", "identity.passwordReset", "identity.mfaEnrolled", "identity.mfaReset", "identity.breakGlassUsed", "identity.tokenTheftDetected",
        "identity.userCreated", "identity.userUpdated", "identity.userDisabled", "identity.userDeleted",
        "config.published", "config.rolledBack", "config.approvalRequested", "config.approved", "config.rejected", "config.lockBroken", "config.imported", "config.exported",
        "rights.grantsChanged", "rights.copyApplied", "rights.copyUndone", "rights.groupChanged", "rights.escalationDenied",
        "commands.requested", "commands.rejected", "commands.approved", "commands.cancelled",
        "alarms.acknowledged", "alarms.closed", "alarms.closedWithoutReport", "alarms.shelved", "alarms.transferred", "alarms.muted",
        "secrets.created", "secrets.replaced", "secrets.deleted", "secrets.readDenied", "secrets.readAggregate",
        "maintenance.started", "maintenance.extended", "maintenance.ended", "maintenance.autoEnded",
        "integrations.packageInstalled", "integrations.packageRemoved", "integrations.connectionEnabled", "integrations.connectionDisabled", "integrations.hostPaired",
        "privacy.personalDataViewed", "privacy.subjectExported", "privacy.subjectErased", "privacy.eventsExported",
        "audit.chainVerificationFailed", "audit.checkpointCreated", "audit.retentionPurged", "audit.exported",
    ];

    [Fact]
    public void AUD_U_001_Action_format_is_validated_and_catalogue_actions_have_translation_keys_in_every_language()
    {
        _catalogue.ShouldAllBe(a => AuditAction.IsValid(a));
        foreach (var bad in new[] { "", "identity", "Identity.signedIn", "identity.signed_in", "identity.signed in", "identity.", ".signedIn", "identity.signedIn1" })
        {
            AuditAction.IsValid(bad).ShouldBeFalse(bad);
        }

        AuditAction.IsValid(null).ShouldBeFalse();
        AuditAction.IsValid("rights.copy.applied").ShouldBeTrue();
        new AuditAction("identity.signedIn").TranslationKey.ShouldBe("audit.action.identity.signedIn");

        var root = RepositoryRoot();
        foreach (var language in new[] { "en", "fr", "ar" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "ui", "packages", "i18n", "locales", language, "app.json")));
            foreach (var action in _catalogue)
            {
                var node = document.RootElement.GetProperty("audit").GetProperty("action");
                foreach (var part in action.Split('.'))
                {
                    node.TryGetProperty(part, out node).ShouldBeTrue($"{language}: audit.action.{action}");
                }

                node.ValueKind.ShouldBe(JsonValueKind.String);
            }
        }
    }

    [Fact]
    public void AUD_U_002_Canonical_serialisation_is_stable_against_golden_vectors()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "audit", "canonical.json")));
        foreach (var vector in vectors.RootElement.GetProperty("rows").EnumerateArray())
        {
            var row = VectorRow(vector.GetProperty("row"));
            Encoding.UTF8.GetString(HashChain.Canonical(row)).ShouldBe(vector.GetProperty("canonical").GetString(), vector.GetProperty("name").GetString());
            var prev = Convert.FromHexString(vector.GetProperty("prevHash").GetString()!);
            Convert.ToHexStringLower(HashChain.ComputeHash(prev, row)).ShouldBe(vector.GetProperty("hash").GetString(), vector.GetProperty("name").GetString());
        }

        // RFC 8785 JCS: sorted members, minimal escapes, ECMAScript numbers.
        foreach (var vector in vectors.RootElement.GetProperty("jcs").EnumerateArray())
        {
            CanonicalJson.Canonicalize(vector.GetProperty("input").GetString()!).ShouldBe(vector.GetProperty("output").GetString());
        }

        HashChain.Genesis("node-1").ShouldBe(SHA256.HashData(Encoding.UTF8.GetBytes("watchpoint-audit:node-1")));
    }

    [Fact]
    public void AUD_U_003_Hash_chaining_starts_at_the_genesis_and_links_consecutive_records()
    {
        var genesis = HashChain.Genesis("node-1");
        var first = HashChain.Seal(Row(1), genesis);
        var second = HashChain.Seal(Row(2), first.Hash);

        first.PrevHash.ShouldBe(genesis);
        first.Hash.Length.ShouldBe(32);
        second.PrevHash.ShouldBe(first.Hash);
        second.Hash.ShouldNotBe(first.Hash);
        HashChain.ComputeHash(genesis, first).ShouldBe(first.Hash);

        // Any field changes the hash; the same row on a different chain does too.
        HashChain.ComputeHash(genesis, first with { Reason = "other" }).ShouldNotBe(first.Hash);
        HashChain.ComputeHash(HashChain.Genesis("node-2"), first).ShouldNotBe(first.Hash);
    }

    [Fact]
    public async Task AUD_U_010_Oversized_details_are_replaced_by_a_truncation_marker_with_the_hash()
    {
        await using var h = new AuditHarness(s => s with { MaxDetailsBytes = 64 });
        await h.StartAsync();
        var big = AuditHarness.Json("{\"text\":\"" + new string('x', 500) + "\"}");
        var small = AuditHarness.Json("{\"ok\":true}");

        await h.WriteAsync(AuditHarness.Record(details: big, severity: AuditSeverity.Critical));
        await h.WriteAsync(AuditHarness.Record(details: small, severity: AuditSeverity.Critical));

        var stored = JsonDocument.Parse(h.Store.Rows[0].DetailsJson!).RootElement;
        stored.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        stored.GetProperty("sha256").GetString().ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(big)))));
        h.Store.Rows[1].DetailsJson.ShouldBe("{\"ok\":true}");
    }

    [Fact]
    public void AUD_U_011_Actor_helpers_describe_users_the_system_and_federation()
    {
        var user = AuditActor.User(AuditHarness.Alice, "Alice", AuditClientKind.Web, "WS-1", "10.0.0.5");
        user.Kind.ShouldBe(ActorKind.User);
        (user.Id, user.Client, user.Workstation, user.IpAddress, user.Via).ShouldBe((AuditHarness.Alice, AuditClientKind.Web, "WS-1", "10.0.0.5", null));

        var system = AuditActor.System("scheduler");
        (system.Kind, system.Id, system.DisplayName, system.Client).ShouldBe((ActorKind.System, null, "scheduler", null));

        var federated = AuditActor.Federated(AuditHarness.Alice, "Bob", "HQ via federation link");
        (federated.Kind, federated.Via).ShouldBe((ActorKind.Federation, "HQ via federation link"));

        var json = JsonSerializer.Serialize(user, AuditJsonContext.Default.AuditActor);
        json.ShouldContain("\"kind\":\"User\"");
        JsonSerializer.Deserialize(json, AuditJsonContext.Default.AuditActor).ShouldBe(user);
    }

    [Fact]
    public void AUD_U_012_Secrets_in_details_are_redacted_by_name_and_by_secret_value_type()
    {
        var details = AuditHarness.Json(
            "{\"password\":\"p1\",\"newPassword\":\"p2\",\"api_token\":\"t\",\"apiKey\":\"k\",\"pin\":\"1234\",\"Authorization\":\"Bearer x\",\"client\":{\"secret\":\"s\",\"name\":\"ok\"}," +
            "\"list\":[{\"token\":\"t2\"},{\"fine\":1}],\"credential\":{\"$secret\":\"vault://abc\"},\"pinned\":true,\"name\":\"visible\"}");

        var json = AuditRecordPreparer.PrepareDetails(details, 64 * 1024)!;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        foreach (var name in new[] { "password", "newPassword", "api_token", "apiKey", "pin", "Authorization", "credential" })
        {
            root.GetProperty(name).GetString().ShouldBe("•••", name);
        }

        root.GetProperty("client").GetProperty("secret").GetString().ShouldBe("•••");
        root.GetProperty("client").GetProperty("name").GetString().ShouldBe("ok");
        root.GetProperty("list")[0].GetProperty("token").GetString().ShouldBe("•••");
        root.GetProperty("list")[1].GetProperty("fine").GetInt32().ShouldBe(1);
        root.GetProperty("pinned").GetBoolean().ShouldBeTrue();
        root.GetProperty("name").GetString().ShouldBe("visible");
        json.ShouldNotContain("p1");
        json.ShouldNotContain("vault://abc");
    }

    [Fact]
    public void AUD_U_019_Swapping_the_logging_sink_for_the_real_sink_needs_no_caller_changes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLoggingAuditSink();
        services.AddSingleton<IIdentityAuditSink, FakeIdentityLogSink>();
        services.AddSingleton<ISecretsAuditSink, FakeSecretsLogSink>();
        services.AddSingleton<IAuditStore>(new InMemoryAuditStore());
        services.AddSingleton<IAuditSigner>(new TestSigner());
        using (var before = services.BuildServiceProvider())
        {
            before.GetRequiredService<IAuditSink>().ShouldBeOfType<LoggingAuditSink>();
        }

        var configuration = new ConfigurationBuilder().Build();
        services.AddWatchpointAudit(configuration, "node-1", Path.Combine(Path.GetTempPath(), "wp-audit-di"));
        using var provider = services.BuildServiceProvider();

        // Callers resolve the same interfaces and now reach the audit trail.
        provider.GetRequiredService<IAuditSink>().ShouldBeOfType<AuditWriter>();
        provider.GetRequiredService<IIdentityAuditSink>().ShouldBeOfType<IdentityAuditAdapter>();
        provider.GetRequiredService<ISecretsAuditSink>().ShouldBeOfType<SecretsAuditAdapter>();
        provider.GetRequiredService<IAuditQuery>().ShouldNotBeNull();
        provider.GetRequiredService<IAuditVerifier>().ShouldNotBeNull();
        provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Count(s => s is AuditWriter or CheckpointService or ContinuousVerifier or AuditRetention).ShouldBe(4);
    }

    [Fact]
    public async Task AUD_U_019_Identity_and_secrets_records_reach_the_audit_trail_through_the_adapters()
    {
        await using var h = new AuditHarness();
        await h.StartAsync();
        var identity = new IdentityAuditAdapter(h.Writer);
        var user = Watchpoint.Kernel.Ids.UserId.From(AuditHarness.Alice);

        await identity.RecordAsync(
            new IdentityAuditRecord("identity.breakGlassUsed", IdentityAuditOutcome.Success, user, Watchpoint.Kernel.Ids.UserId.From(Guid.CreateVersion7()), "fire", IdentityAuditSeverity.Critical,
                new Dictionary<string, string?> { ["client"] = "desktop", ["password"] = "must-not-appear" }, h.Clock.GetUtcNow()),
            CancellationToken.None);

        var row = h.Store.Rows.ShouldHaveSingleItem();
        row.Action.ShouldBe("identity.breakGlassUsed");
        row.Severity.ShouldBe((short)AuditSeverity.Critical);
        row.Reason.ShouldBe("fire");
        row.ActorId.ShouldBe(AuditHarness.Alice);
        row.DetailsJson!.ShouldNotContain("must-not-appear");
    }

    private static AuditRow Row(long sequence) =>
        new(0, "node-1", sequence, new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 15, 9, 0, 1, TimeSpan.Zero), "identity.signedIn", 0, 0,
            "{\"kind\":\"User\",\"displayName\":\"Alice\"}", null, "[]", [], null, null, null, [], []);

    private static AuditRow VectorRow(JsonElement e) =>
        new(0, e.GetProperty("chainId").GetString()!, e.GetProperty("sequence").GetInt64(), e.GetProperty("occurredAt").GetDateTimeOffset(), e.GetProperty("recordedAt").GetDateTimeOffset(),
            e.GetProperty("action").GetString()!, e.GetProperty("outcome").GetInt16(), e.GetProperty("severity").GetInt16(), e.GetProperty("actor").GetRawText(), null,
            e.GetProperty("objects").GetRawText(), [], e.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
            e.TryGetProperty("details", out var details) ? details.GetRawText() : null, e.TryGetProperty("correlationId", out var c) ? c.GetGuid() : null, [], []);

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Architecture")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class FakeIdentityLogSink : IIdentityAuditSink
    {
        public ValueTask RecordAsync(IdentityAuditRecord record, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class FakeSecretsLogSink : ISecretsAuditSink
    {
        public void Record(SecretAuditRecord record)
        {
        }
    }
}
