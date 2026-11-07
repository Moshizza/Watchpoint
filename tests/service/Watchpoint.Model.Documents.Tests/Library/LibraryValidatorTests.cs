using System.Security.Cryptography;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Documents.Library;
using Xunit;

namespace Watchpoint.Model.Documents.Tests.Library;

[Trait("layer", "U")]
[Trait("spec", "SCM")]
public sealed class LibraryValidatorTests : IDisposable
{
    private readonly TempLibrary _lib = new();

    public void Dispose() => _lib.Dispose();

    [Fact]
    public void SCM_U_021_Library_missing_capability_reported()
    {
        _lib.Type("test.thing", """[{"id":"health","version":"^1.0"},{"id":"foo","version":"^1.0"}]""");

        var problem = _lib.Validate().WithCode(ProblemCodes.CapabilityMissing).ShouldHaveSingleItem();

        problem.Severity.ShouldBe(ProblemSeverity.Error);
        problem.SourcePath.ShouldBe("device-types/test.thing.json");
        problem.JsonPath.ShouldBe("/capabilities/1/id");
        problem.Args["capability"].ShouldBe("foo");
    }

    [Fact]
    public void SCM_U_022_Library_version_range_unsatisfied()
    {
        _lib.Capability("gadget").Type("test.thing", """[{"id":"health","version":"^1.0"},{"id":"gadget","version":"^2.0"}]""");

        var problem = _lib.Validate().WithCode(ProblemCodes.CapabilityVersionUnsatisfied).ShouldHaveSingleItem();

        problem.JsonPath.ShouldBe("/capabilities/1/version");
        problem.Args["available"].ShouldBe("1.0.0");
    }

    [Fact]
    public void SCM_U_023_Library_extends_merges_capabilities()
    {
        _lib.Type("test.battery-door", """[{"id":"battery","version":"^1.0"}]""", primary: "battery.level",
            extends: """{"id":"ac.door","version":"^1.0"}""", symbol: "ac.door");
        var set = new LibrarySet(_lib.Load(), [TempLibrary.Standard]);

        var resolved = LibraryValidator.Resolve(set, TypeId.FromTrusted("test.battery-door")).ShouldNotBeNull();

        resolved.Capabilities.Select(c => c.Id.Value).ShouldBe(
            ["health", "doorPosition", "lockable", "openable", "doorMode", "accessEvents", "tamper", "battery"]);
        resolved.AttributeNames.ShouldContain("heldOpenAfter");
        resolved.DefaultAlarmRules.ShouldContain("std.ac.door.forced");
        resolved.Chain.Select(t => t.Value).ShouldBe(["test.battery-door", "ac.door"]);
        new LibraryValidator().Validate(set, new LibraryValidationOptions())
            .Problems.Where(p => p.Severity == ProblemSeverity.Error && p.Code.Value != ProblemCodes.TranslationMissing).ShouldBeEmpty();
    }

    [Fact]
    public void SCM_U_024_Library_extends_cycle_detected()
    {
        _lib.Type("test.a", extends: """{"id":"test.b","version":"^1.0"}""")
            .Type("test.b", extends: """{"id":"test.a","version":"^1.0"}""");

        var problems = _lib.Validate().WithCode(ProblemCodes.TypeExtendsCycle).ToList();

        problems.Count.ShouldBe(2);
        problems.ShouldAllBe(p => p.JsonPath == "/extends");
    }

    [Fact]
    public void SCM_U_025_Library_depth_over_3_rejected()
    {
        _lib.Type("test.t1", extends: """{"id":"test.t2","version":"^1.0"}""")
            .Type("test.t2", extends: """{"id":"test.t3","version":"^1.0"}""")
            .Type("test.t3", extends: """{"id":"test.t4","version":"^1.0"}""")
            .Type("test.t4", extends: """{"id":"test.t5","version":"^1.0"}""")
            .Type("test.t5");

        var problem = _lib.Validate().WithCode(ProblemCodes.TypeExtendsTooDeep).ShouldHaveSingleItem();

        problem.SourcePath.ShouldBe("device-types/test.t1.json"); // 4 hops; test.t2 has 3 and is accepted
    }

    [Fact]
    public void SCM_U_026_Library_health_missing()
    {
        _lib.Type("test.thing", """[{"id":"battery","version":"^1.0"}]""", primary: "battery.level");

        _lib.Validate().WithCode(ProblemCodes.TypeHealthMissing).ShouldHaveSingleItem().JsonPath.ShouldBe("/capabilities");
    }

    [Fact]
    public void SCM_U_027_Library_primary_property_unknown()
    {
        _lib.Type("test.thing", primary: "health.temperature");

        _lib.Validate().WithCode(ProblemCodes.TypePrimaryPropertyUnknown).ShouldHaveSingleItem().JsonPath.ShouldBe("/display/primaryProperty");
    }

    [Fact]
    public void SCM_U_028_Library_override_value_unknown()
    {
        _lib.Type("test.lock", """[{"id":"health","version":"^1.0"},{"id":"lockable","version":"^1.0"}]""",
            extra: ""","stateSummary":{"overrides":[{"property":"lockable.lockState","values":["unlocked","open"],"state":"normalActive"}]}""");

        var problem = _lib.Validate().WithCode(ProblemCodes.TypeOverrideValueUnknown).ShouldHaveSingleItem();

        problem.JsonPath.ShouldBe("/stateSummary/overrides/0/values/1");
        problem.Args["value"].ShouldBe("open");
    }

    [Fact]
    public void SCM_U_029_Library_exclude_unknown_member()
    {
        _lib.Type("test.door", """[{"id":"health","version":"^1.0"},{"id":"doorMode","version":"^1.0","exclude":{"commands":["explode"],"enumValues":{"mode":["emergencyClose","party"]}}}]""");

        var problems = _lib.Validate().WithCode(ProblemCodes.TypeExcludeUnknown).ToList();

        problems.Select(p => p.JsonPath).ShouldBe(["/capabilities/1/exclude/commands/0", "/capabilities/1/exclude/enumValues/mode/1"]);
    }

    [Fact]
    public void SCM_U_030_Library_expected_effect_unknown_property()
    {
        // openable.open expects doorPosition.position, which this type does not have.
        _lib.Type("test.opener", """[{"id":"health","version":"^1.0"},{"id":"openable","version":"^1.0"}]""");

        var problem = _lib.Validate().WithCode(ProblemCodes.CommandEffectPropertyUnknown).ShouldHaveSingleItem();

        problem.JsonPath.ShouldBe("/capabilities/1");
        problem.Args["command"].ShouldBe("openable.open");
        problem.Args["property"].ShouldBe("doorPosition.position");
    }

    [Fact]
    public void SCM_U_031_Library_writable_without_setter()
    {
        _lib.Capability("dimmer", properties: """[{"id":"level","valueType":"integer","writable":true},{"id":"mode","valueType":"string","writable":true}]""",
            commands: """[{"id":"setMode"}]""");

        var problem = _lib.Validate().WithCode(ProblemCodes.PropertyWritableWithoutSetter).ShouldHaveSingleItem();

        problem.JsonPath.ShouldBe("/properties/0/writable");
        problem.Args["command"].ShouldBe("setLevel");
    }

    [Fact]
    public void SCM_U_032_i18n_missing_en_key_is_error_missing_fr_is_warning()
    {
        _lib.Capability("gadget")
            .Write("i18n/en.json", """{"capabilities":{"gadget":{"name":"Gadget"}}}""")
            .Write("i18n/fr.json", """{"capabilities":{"gadget":{"name":"Gadget","description":"Un gadget."}}}""")
            .Write("i18n/ar.json", """{"capabilities":{"gadget":{"name":"أداة","description":"أداة."}}}""");

        var problems = _lib.Validate().WithCode(ProblemCodes.TranslationMissing).ToList();

        var en = problems.ShouldHaveSingleItem();
        en.Severity.ShouldBe(ProblemSeverity.Error);
        en.Args["culture"].ShouldBe("en");
        en.Args["key"].ShouldBe("capabilities.gadget.description");

        _lib.Write("i18n/en.json", """{"capabilities":{"gadget":{"name":"Gadget","description":"A gadget."}}}""")
            .Write("i18n/fr.json", """{"capabilities":{"gadget":{"name":"Gadget"}}}""");
        var fr = _lib.Validate().WithCode(ProblemCodes.TranslationMissing).ShouldHaveSingleItem();
        fr.Severity.ShouldBe(ProblemSeverity.Warning);
        fr.Args["culture"].ShouldBe("fr");
    }

    [Fact]
    public void SCM_U_033_i18n_strict_missing_ar_key_is_error()
    {
        _lib.Capability("gadget")
            .Write("i18n/en.json", """{"capabilities":{"gadget":{"name":"Gadget","description":"A gadget."}}}""")
            .Write("i18n/fr.json", """{"capabilities":{"gadget":{"name":"Gadget","description":"Un gadget."}}}""")
            .Write("i18n/ar.json", """{"capabilities":{"gadget":{"name":"أداة"}}}""");

        _lib.Validate().WithCode(ProblemCodes.TranslationMissing).ShouldHaveSingleItem().Severity.ShouldBe(ProblemSeverity.Warning);
        var strict = _lib.Validate(strictI18n: true);

        var ar = strict.WithCode(ProblemCodes.TranslationMissing).ShouldHaveSingleItem();
        ar.Severity.ShouldBe(ProblemSeverity.Error);
        ar.Args["culture"].ShouldBe("ar");
        strict.HasErrors.ShouldBeTrue();
    }

    [Fact]
    public void SCM_U_034_Symbols_missing_state_SVG_reported()
    {
        _lib.Type("test.thing", symbol: "test.thing");
        foreach (var state in LibraryValidator.SymbolStates.Where(s => s != "maintenance"))
        {
            _lib.Write($"symbols/test.thing/{state}.svg", Svg(string.Empty));
        }

        var problem = _lib.Validate().WithCode(ProblemCodes.SymbolMissing).ShouldHaveSingleItem();

        problem.JsonPath.ShouldBe("/display/symbol");
        problem.Args["state"].ShouldBe("maintenance");
    }

    [Theory]
    [InlineData("script", "<script>alert(1)</script>", "element <script>")]
    [InlineData("onload", "<circle r=\"4\" onload=\"alert(1)\"/>", "event handler onload")]
    [InlineData("external", "<use href=\"https://evil.example/x.svg#a\"/>", "external reference")]
    [InlineData("image", "<image href=\"#local\" width=\"4\" height=\"4\"/>", "element <image>")]
    public void SCM_U_035_Symbols_unsafe_SVG_rejected(string name, string content, string reason)
    {
        _lib.Write($"symbols/test.{name}/default.svg", Svg(content)).Write("symbols/test.safe/default.svg", Svg("<use href=\"#local\"/>"));

        var problem = _lib.Validate().WithCode(ProblemCodes.SymbolUnsafe).ShouldHaveSingleItem();

        problem.SourcePath.ShouldBe($"symbols/test.{name}/default.svg");
        ((string)problem.Args["reason"]!).ShouldStartWith(reason);
    }

    [Fact]
    public void SCM_U_036_Package_folder_unlisted_missing_and_hash_mismatch()
    {
        _lib.Write("lib/Acme.Connector.dll", "assembly bytes")
            .Write("docs/readme.md", "unlisted")
            .Write("i18n/en.json", """{"packages":{"acme":{"connector":{"name":"Acme"}}}}""");
        var good = Sha256("assembly bytes");
        _lib.Write("manifest.json", $$"""
            {
              "$schema": "https://schemas.watchpoint.dev/manifest/1.json",
              "id": "acme.connector", "version": "1.0.0", "vendor": "Acme", "category": "generic",
              "contract": "^1.0", "minWatchpointVersion": "1.0.0", "platforms": ["linux-x64"],
              "nativeCode": false, "isolation": "inProcessAllowed", "licence": { "class": "included" },
              "connectors": [{ "class": "Acme.Connector::Acme.Connector.Main", "serverType": "generic.server", "supportedVendorVersions": "*",
                               "features": ["discovery"], "settingsSchema": { "type": "object", "properties": {} }, "secretsSchema": { "fields": [] } }],
              "types": [], "capabilities": [], "ruleTemplates": [],
              "i18n": "i18n/",
              "files": [
                { "path": "lib/Acme.Connector.dll", "sha256": "{{good}}" },
                { "path": "i18n/en.json", "sha256": "{{new string('0', 64)}}" },
                { "path": "lib/Missing.dll", "sha256": "{{good}}" }
              ]
            }
            """);

        var report = _lib.Validate();

        report.WithCode(ProblemCodes.ManifestFileUnlisted).ShouldHaveSingleItem().Args["file"].ShouldBe("docs/readme.md");
        report.WithCode(ProblemCodes.ManifestFileMissing).ShouldHaveSingleItem().Args["file"].ShouldBe("lib/Missing.dll");
        report.WithCode(ProblemCodes.ManifestFileHashMismatch).ShouldHaveSingleItem().Args["file"].ShouldBe("i18n/en.json");
        report.WithCode(ProblemCodes.ManifestConnectorAssemblyUnlisted).ShouldBeEmpty();
    }

    private static string Svg(string content) => $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">{content}</svg>""";

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}
