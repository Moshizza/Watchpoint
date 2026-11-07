using System.Text;
using System.Text.Json;
using Shouldly;
using Watchpoint.Kernel.Results;
using Xunit;

namespace Watchpoint.Model.Documents.Tests;

public sealed class DocumentReaderTests
{
    private static readonly string SchemasDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../schemas"));
    private readonly DocumentReader _reader = new(SchemasDir);

    [Fact]
    public void SCM_U_001_Valid_capability_parses()
    {
        string json = """
        {
          "$schema": "https://schemas.watchpoint.dev/capability/1.json",
          "id": "doorPosition",
          "version": "1.0.0",
          "properties": [
            {
              "id": "position",
              "valueType": "enum",
              "enumValues": [
                { "code": "closed",   "severity": "normal" },
                { "code": "open",     "severity": "normalActive" },
                { "code": "heldOpen", "severity": "warning" },
                { "code": "forced",   "severity": "alarm" },
                { "code": "unknown",  "severity": "unknown" }
              ],
              "writable": false,
              "history": { "mode": "everyChange" },
              "staleAfter": "PT10M"
            }
          ],
          "commands": [],
          "events": [
            { "id": "forced",   "severity": "alarm",   "payload": { "type": "object", "properties": {} } },
            { "id": "heldOpen", "severity": "warning", "payload": { "type": "object", "properties": { "openFor": { "type": "duration" } } } }
          ]
        }
        """;

        var res = _reader.ReadCapability(Encoding.UTF8.GetBytes(json), "cap.json");
        res.IsSuccess.ShouldBeTrue();
        var doc = res.Value;
        doc.Id.Value.ShouldBe("doorPosition");
        doc.Properties.Count.ShouldBe(1);
        doc.Events.Count.ShouldBe(2);
    }

    [Fact]
    public void SCM_U_002_Unknown_top_level_property_rejected()
    {
        string json = """
        {
          "$schema": "https://schemas.watchpoint.dev/capability/1.json",
          "id": "doorPosition",
          "version": "1.0.0",
          "foo": 1,
          "properties": [],
          "commands": [],
          "events": []
        }
        """;

        var res = _reader.ReadCapability(Encoding.UTF8.GetBytes(json), "cap.json");
        res.IsSuccess.ShouldBeFalse();
        res.Error.FieldErrors.ShouldContain(e => e.Path.Contains("foo"));
    }

    [Fact]
    public void SCM_U_003_X_extensions_accepted()
    {
        string json = """
        {
          "$schema": "https://schemas.watchpoint.dev/capability/1.json",
          "id": "doorPosition",
          "version": "1.0.0",
          "x-note": "a",
          "properties": [],
          "commands": [],
          "events": []
        }
        """;

        var res = _reader.ReadCapability(Encoding.UTF8.GetBytes(json), "cap.json");
        res.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void SCM_U_004_Enum_property_without_values_rejected()
    {
        string json = """
        {
          "$schema": "https://schemas.watchpoint.dev/capability/1.json",
          "id": "doorPosition",
          "version": "1.0.0",
          "properties": [
            {
              "id": "position",
              "valueType": "enum"
            }
          ],
          "commands": [],
          "events": []
        }
        """;

        var res = _reader.ReadCapability(Encoding.UTF8.GetBytes(json), "cap.json");
        res.IsSuccess.ShouldBeFalse();
        res.Error.FieldErrors.ShouldContain(e => e.Path.Contains("properties/0") || e.Path.Contains("enumValues"));
    }

    [Fact]
    public void SCM_U_005_Duplicate_enum_codes_rejected()
    {
        string json = """
        {
          "$schema": "https://schemas.watchpoint.dev/capability/1.json",
          "id": "doorPosition",
          "version": "1.0.0",
          "properties": [
            {
              "id": "position",
              "valueType": "enum",
              "enumValues": [
                { "code": "open", "severity": "normal" },
                { "code": "open", "severity": "alarm" }
              ]
            }
          ],
          "commands": [],
          "events": []
        }
        """;

        var res = _reader.ReadCapability(Encoding.UTF8.GetBytes(json), "cap.json");
        res.IsSuccess.ShouldBeFalse();
        res.Error.FieldErrors.ShouldContain(e => e.Code == CommonErrorCodes.ValueDuplicate);
    }

    [Fact]
    public void SCM_U_006_Unit_on_non_number_rejected()
    {
        string json = """
        {
          "$schema": "https://schemas.watchpoint.dev/capability/1.json",
          "id": "doorPosition",
          "version": "1.0.0",
          "properties": [
            {
              "id": "position",
              "valueType": "string",
              "unit": "m"
            }
          ],
          "commands": [],
          "events": []
        }
        """;

        var res = _reader.ReadCapability(Encoding.UTF8.GetBytes(json), "cap.json");
        res.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void SCM_U_007_Range_min_greater_than_max_rejected()
    {
        string json = """
        {
          "$schema": "https://schemas.watchpoint.dev/capability/1.json",
          "id": "doorPosition",
          "version": "1.0.0",
          "properties": [
            {
              "id": "level",
              "valueType": "integer",
              "range": { "min": 100, "max": 10 }
            }
          ],
          "commands": [],
          "events": []
        }
        """;

        var res = _reader.ReadCapability(Encoding.UTF8.GetBytes(json), "cap.json");
        res.IsSuccess.ShouldBeFalse();
        res.Error.FieldErrors.ShouldContain(e => e.Code == CommonErrorCodes.ValueOutOfRange);
    }

    [Fact]
    public void SCM_U_008_Deadband_on_non_number_rejected()
    {
        string json = """
        {
          "$schema": "https://schemas.watchpoint.dev/capability/1.json",
          "id": "doorPosition",
          "version": "1.0.0",
          "properties": [
            {
              "id": "position",
              "valueType": "string",
              "history": { "mode": "deadband" }
            }
          ],
          "commands": [],
          "events": []
        }
        """;

        var res = _reader.ReadCapability(Encoding.UTF8.GetBytes(json), "cap.json");
        res.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void SCM_U_009_Command_timeout_over_PT10M_rejected()
    {
        string json = """
        {
          "$schema": "https://schemas.watchpoint.dev/capability/1.json",
          "id": "doorPosition",
          "version": "1.0.0",
          "properties": [],
          "commands": [
            {
              "id": "open",
              "timeout": "PT11M"
            }
          ],
          "events": []
        }
        """;

        var res = _reader.ReadCapability(Encoding.UTF8.GetBytes(json), "cap.json");
        res.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void SCM_U_010_Invalid_duration_format_rejected()
    {
        string json = """
        {
          "$schema": "https://schemas.watchpoint.dev/capability/1.json",
          "id": "doorPosition",
          "version": "1.0.0",
          "properties": [
            {
              "id": "position",
              "valueType": "string",
              "staleAfter": "10s"
            }
          ],
          "commands": [],
          "events": []
        }
        """;

        var res = _reader.ReadCapability(Encoding.UTF8.GetBytes(json), "cap.json");
        res.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void SCM_U_011_Parameter_schema_unsupported_keyword_rejected()
    {
        string json = """
        {
          "type": "object",
          "properties": {
            "mode": { "type": "string" }
          },
          "oneOf": []
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var res = ParameterSchema.Parse(doc.RootElement);
        res.IsSuccess.ShouldBeFalse();
        res.Error.FieldErrors.ShouldContain(e => e.Path.Contains("oneOf"));
    }

    [Fact]
    public void SCM_U_012_Parameter_schema_nested_object_depth_2_rejected()
    {
        string json = """
        {
          "type": "object",
          "properties": {
            "level1": {
              "type": "object",
              "properties": {
                "level2": {
                  "type": "object",
                  "properties": {
                    "level3": { "type": "string" }
                  }
                }
              }
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var res = ParameterSchema.Parse(doc.RootElement);
        res.IsSuccess.ShouldBeFalse();
        res.Error.FieldErrors.ShouldContain(e => e.Code == CommonErrorCodes.ValueOutOfRange);
    }

    [Fact]
    public void SCM_U_013_Parameter_schema_validates_values()
    {
        string schemaJson = """
        {
          "type": "object",
          "properties": {
            "mode":     { "type": "string", "enum": ["full", "partial"], "default": "full" },
            "duration": { "type": "duration", "minimum": "PT1S", "maximum": "PT1H", "default": "PT5S" },
            "level":    { "type": "integer", "minimum": 0, "maximum": 100 },
            "host":     { "type": "string", "format": "hostname", "maxLength": 253 },
            "port":     { "type": "integer", "minimum": 1, "maximum": 65535, "default": 443 },
            "useTls":   { "type": "boolean", "default": true },
            "camera":   { "type": "entityRef", "entity": "device", "deviceCategory": "video" },
            "tags":     { "type": "array", "items": { "type": "string" }, "maxItems": 20 }
          },
          "required": ["mode"]
        }
        """;

        using var sDoc = JsonDocument.Parse(schemaJson);
        var schema = ParameterSchema.Parse(sDoc.RootElement).Value;

        // invalid mode
        using var val1 = JsonDocument.Parse("""{ "mode": "x" }""");
        var errors1 = schema.Validate(val1.RootElement);
        errors1.ShouldContain(e => e.Path == "/mode" && e.Code == CommonErrorCodes.ValueNotAllowed);

        // missing required mode
        using var val2 = JsonDocument.Parse("""{ "port": 80 }""");
        var errors2 = schema.Validate(val2.RootElement);
        errors2.ShouldContain(e => e.Path == "/mode" && e.Code == CommonErrorCodes.ValueRequired);
    }

    [Fact]
    public void SCM_U_014_Parameter_schema_defaults_applied()
    {
        string schemaJson = """
        {
          "type": "object",
          "properties": {
            "mode":     { "type": "string", "enum": ["full", "partial"], "default": "full" },
            "duration": { "type": "duration", "default": "PT5S" },
            "port":     { "type": "integer", "default": 443 },
            "useTls":   { "type": "boolean", "default": true }
          }
        }
        """;

        using var sDoc = JsonDocument.Parse(schemaJson);
        var schema = ParameterSchema.Parse(sDoc.RootElement).Value;

        using var val = JsonDocument.Parse("{}");
        var withDefaults = schema.ApplyDefaults(val.RootElement);

        withDefaults.GetProperty("mode").GetString().ShouldBe("full");
        withDefaults.GetProperty("duration").GetString().ShouldBe("PT5S");
        withDefaults.GetProperty("port").GetInt32().ShouldBe(443);
        withDefaults.GetProperty("useTls").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void SCM_U_015_VisibleWhen_referencing_unknown_field_rejected()
    {
        string schemaJson = """
        {
          "type": "object",
          "properties": {
            "port": { "type": "integer" }
          },
          "x-wp-ui": {
            "visibleWhen": {
              "port": { "field": "unknownField", "equals": true }
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(schemaJson);
        var res = ParameterSchema.Parse(doc.RootElement);
        res.IsSuccess.ShouldBeFalse();
        res.Error.FieldErrors.ShouldContain(e => e.Path.Contains("visibleWhen/port/field"));
    }

    [Fact]
    public void SCM_U_016_Device_type_example_parses()
    {
        string json = """
        {
          "$schema": "https://schemas.watchpoint.dev/device-type/1.json",
          "id": "ac.door",
          "version": "1.0.0",
          "role": "device",
          "category": "accessControl",
          "extends": null,
          "capabilities": [
            { "id": "health", "version": "^1.0" },
            { "id": "doorPosition", "version": "^1.0" },
            { "id": "lockable", "version": "^1.0" },
            { "id": "openable", "version": "^1.0" },
            { "id": "doorMode", "version": "^1.0", "exclude": { "commands": [] , "enumValues": { "mode": ["emergencyClose"] } } },
            { "id": "accessEvents", "version": "^1.0" },
            { "id": "tamper", "version": "^1.0", "optional": true }
          ],
          "attributes": {
            "type": "object",
            "properties": {
              "heldOpenAfter": { "type": "duration", "default": "PT60S", "minimum": "PT5S", "maximum": "PT1H" }
            }
          },
          "allowedChildTypes": ["ac.reader", "ac.lock", "generic.binary-input"],
          "licenceClass": "full",
          "display": {
            "icon": "door",
            "symbol": "ac.door",
            "primaryProperty": "doorPosition.position"
          },
          "stateSummary": {
            "overrides": [
              { "property": "lockable.lockState", "values": ["unlocked"], "state": "normalActive" }
            ]
          },
          "defaultAlarmRules": ["std.ac.door.forced", "std.ac.door.heldOpen", "std.health.offline"],
          "defaultLinks": [{ "kind": "cameraViews", "peerCategory": "video" }]
        }
        """;

        var res = _reader.ReadDeviceType(Encoding.UTF8.GetBytes(json), "type.json");
        res.IsSuccess.ShouldBeTrue();
        var doc = res.Value;
        doc.Id.Value.ShouldBe("ac.door");
        doc.Role.ShouldBe(TypeRole.Device);
        doc.Category.ShouldBe(Category.AccessControl);
        doc.Capabilities.Count.ShouldBe(7);
    }

    [Fact]
    public void SCM_U_017_Device_type_role_category_invalid_rejected()
    {
        string json = """
        {
          "$schema": "https://schemas.watchpoint.dev/device-type/1.json",
          "id": "ac.door",
          "version": "1.0.0",
          "role": "invalidRole",
          "category": "invalidCategory",
          "extends": null,
          "capabilities": [
            { "id": "health", "version": "^1.0" }
          ],
          "licenceClass": "full",
          "display": {
            "icon": "door",
            "symbol": "ac.door",
            "primaryProperty": "health.connectivity"
          }
        }
        """;

        var res = _reader.ReadDeviceType(Encoding.UTF8.GetBytes(json), "type.json");
        res.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void SCM_U_018_Manifest_example_parses()
    {
        string json = """
        {
          "$schema": "https://schemas.watchpoint.dev/manifest/1.json",
          "id": "watchpoint.integration.simulator",
          "version": "1.0.0",
          "vendor": "Watchpoint",
          "category": "generic",
          "contract": "^1.0",
          "minWatchpointVersion": "1.0.0",
          "platforms": ["win-x64", "linux-x64", "linux-arm64"],
          "nativeCode": false,
          "isolation": "inProcessAllowed",
          "licence": { "featureKey": "int.simulator", "class": "included" },
          "connectors": [
            {
              "class": "Watchpoint.Integration.Simulator::Watchpoint.Integration.Simulator.SimulatorConnector",
              "serverType": "sim.server",
              "supportedVendorVersions": "*",
              "features": ["discovery", "liveObservation", "stateRefresh", "commandExecution", "eventCatchUp", "diagnostics"],
              "settingsSchema": "settings/simulator.settings.json",
              "secretsSchema": { "fields": [] },
              "limits": { "devicesPerConnection": 50000, "commandsPerSecond": 500 }
            }
          ],
          "types": ["types/sim.server.json"],
          "capabilities": [],
          "ruleTemplates": [],
          "symbols": "symbols/",
          "i18n": "i18n/",
          "docs": "docs/",
          "privacy": { "personalData": [] },
          "files": [
            { "path": "lib/Watchpoint.Integration.Simulator.dll", "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" }
          ]
        }
        """;

        var res = _reader.ReadManifest(Encoding.UTF8.GetBytes(json), "manifest.json");
        res.IsSuccess.ShouldBeTrue();
        var doc = res.Value;
        doc.Id.Value.ShouldBe("watchpoint.integration.simulator");
        doc.Connectors.Count.ShouldBe(1);
    }

    [Fact]
    public void SCM_U_019_Manifest_native_code_without_isolatedRequired_rejected()
    {
        string json = """
        {
          "$schema": "https://schemas.watchpoint.dev/manifest/1.json",
          "id": "watchpoint.integration.simulator",
          "version": "1.0.0",
          "vendor": "Watchpoint",
          "category": "generic",
          "contract": "^1.0",
          "minWatchpointVersion": "1.0.0",
          "platforms": ["win-x64"],
          "nativeCode": true,
          "isolation": "inProcessAllowed",
          "licence": { "class": "included" },
          "connectors": [
            {
              "class": "Assembly::Type",
              "serverType": "sim.server",
              "supportedVendorVersions": "*",
              "features": ["discovery"],
              "settingsSchema": "settings.json",
              "secretsSchema": { "fields": [] }
            }
          ],
          "types": [],
          "capabilities": [],
          "ruleTemplates": [],
          "files": []
        }
        """;

        var res = _reader.ReadManifest(Encoding.UTF8.GetBytes(json), "manifest.json");
        res.IsSuccess.ShouldBeFalse();
        res.Error.FieldErrors.ShouldContain(e => e.Code.Value == "scm.manifest.native_requires_isolation");
    }

    [Fact]
    public void SCM_U_020_Manifest_class_format_invalid_rejected()
    {
        string json = """
        {
          "$schema": "https://schemas.watchpoint.dev/manifest/1.json",
          "id": "watchpoint.integration.simulator",
          "version": "1.0.0",
          "vendor": "Watchpoint",
          "category": "generic",
          "contract": "^1.0",
          "minWatchpointVersion": "1.0.0",
          "platforms": ["win-x64"],
          "nativeCode": false,
          "isolation": "inProcessAllowed",
          "licence": { "class": "included" },
          "connectors": [
            {
              "class": "Foo.Bar",
              "serverType": "sim.server",
              "supportedVendorVersions": "*",
              "features": ["discovery"],
              "settingsSchema": "settings.json",
              "secretsSchema": { "fields": [] }
            }
          ],
          "types": [],
          "capabilities": [],
          "ruleTemplates": [],
          "files": []
        }
        """;

        var res = _reader.ReadManifest(Encoding.UTF8.GetBytes(json), "manifest.json");
        res.IsSuccess.ShouldBeFalse();
        res.Error.FieldErrors.ShouldContain(e => e.Path.Contains("class"));
    }
}
