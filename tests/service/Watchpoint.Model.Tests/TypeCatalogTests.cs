using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Model.Internal;

namespace Watchpoint.Model.Tests;

public class TypeCatalogTests
{
    private static (TypeCatalog Catalog, LoadedLibrary Library) CreateCatalog()
    {
        var reader = DocumentReaderFactory.FromEmbeddedSchemas();
        var library = StandardLibraryLoader.Load(reader, NullLogger.Instance).Value;
        var clock = TestTime.CreateProvider();
        var catalog = new TypeCatalog(library, null, reader, NullLogger<TypeCatalog>.Instance, clock, new ModelMetrics());
        return (catalog, library);
    }

    [Fact]
    public void MOD_U_001_Standard_library_loads()
    {
        var (catalog, _) = CreateCatalog();
        var types = catalog.List(new TypeQuery());

        types.ShouldContain(t => t.Ref.Id.Value == "ac.door");
        types.ShouldContain(t => t.Ref.Id.Value == "generic.analog-input");
        types.Count.ShouldBeGreaterThanOrEqualTo(7);
    }

    [Fact]
    public void MOD_U_002_Invalid_standard_library_is_fatal()
    {
        var folder = new Watchpoint.Model.Documents.Library.LibraryFolder
        {
            Root = "test",
            Capabilities = [],
            Types = [
                new Watchpoint.Model.Documents.Library.SourceDocument<DeviceTypeDocument>(
                    "device-types/test.json",
                    new DeviceTypeDocument(
                        TypeId.FromTrusted("test.type"),
                        SemVersion.Parse("1.0.0").Value,
                        TypeRole.Device,
                        Category.Generic,
                        null,
                        [new CapabilityUse(CapabilityId.FromTrusted("missingCap"), SemVersionRange.Parse("*").Value)],
                        null,
                        [],
                        LicenceClass.Free,
                        new TypeDisplay("icon", "symbol", "prop"),
                        null,
                        null,
                        null))
            ],
            Bundles = new Dictionary<string, Watchpoint.Model.Documents.Library.SourceDocument<JsonElement>>(),
            Symbols = new Dictionary<string, IReadOnlyDictionary<string, string>>(),
            Manifest = null,
            LoadProblems = [],
        };

        var validator = new Watchpoint.Model.Documents.Library.LibraryValidator();
        var report = validator.Validate(new Watchpoint.Model.Documents.Library.LibrarySet(folder, []), new Watchpoint.Model.Documents.Library.LibraryValidationOptions { StrictI18n = false });
        report.Problems.ShouldContain(p => p.Severity == Watchpoint.Model.Documents.Library.ProblemSeverity.Error);
    }

    [Fact]
    public void MOD_U_003_Flattening_capabilities_union()
    {
        var (catalog, _) = CreateCatalog();
        var door = catalog.Get(new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value)).Value;

        var customType = new DeviceTypeDocument(
            TypeId.FromTrusted("vendor.x.door"),
            SemVersion.Parse("1.0.0").Value,
            TypeRole.Device,
            Category.AccessControl,
            new TypeReference(TypeId.FromTrusted("ac.door"), SemVersionRange.Parse("^1.0.0").Value),
            [new CapabilityUse(CapabilityId.FromTrusted("battery"), SemVersionRange.Parse("^1.0.0").Value)],
            null,
            [],
            LicenceClass.Full,
            door.Display,
            null,
            null,
            null);

        var resolved = TypeResolver.Resolve(
            customType,
            "vendor.x",
            (id, range) => id.Value == "ac.door" ? catalog.Get(new TypeRef(id, SemVersion.Parse("1.0.0").Value)).Map(d => ToDoc(d)) : ModelErrors.TypeNotFoundError(id, SemVersion.Parse("1.0.0").Value),
            (id, range) => catalog.GetCapability(id, SemVersion.Parse("1.0.0").Value));

        resolved.IsSuccess.ShouldBeTrue();
        var def = resolved.Value;
        def.Has(CapabilityId.FromTrusted("battery")).ShouldBeTrue();
        def.Has(CapabilityId.FromTrusted("health")).ShouldBeTrue();
        def.Has(CapabilityId.FromTrusted("doorPosition")).ShouldBeTrue();
    }

    [Fact]
    public void MOD_U_004_Flattening_descendant_cannot_lower_version_range()
    {
        var parentRange = SemVersionRange.Parse("^1.2").Value;
        var childRange = SemVersionRange.Parse("^1.0").Value;

        var parentDoc = new DeviceTypeDocument(
            TypeId.FromTrusted("vendor.parent"),
            SemVersion.Parse("1.0.0").Value,
            TypeRole.Device,
            Category.Generic,
            null,
            [new CapabilityUse(CapabilityId.FromTrusted("health"), parentRange)],
            null,
            [],
            LicenceClass.Free,
            new TypeDisplay("i", "s", "p"),
            null,
            null,
            null);

        var childDoc = new DeviceTypeDocument(
            TypeId.FromTrusted("vendor.child"),
            SemVersion.Parse("1.0.0").Value,
            TypeRole.Device,
            Category.Generic,
            new TypeReference(parentDoc.Id, SemVersionRange.Parse("*").Value),
            [new CapabilityUse(CapabilityId.FromTrusted("health"), childRange)],
            null,
            [],
            LicenceClass.Free,
            new TypeDisplay("i", "s", "p"),
            null,
            null,
            null);

        var (catalog, _) = CreateCatalog();
        var resolved = TypeResolver.Resolve(
            childDoc,
            "vendor",
            (id, range) => id == parentDoc.Id ? parentDoc : ModelErrors.TypeNotFoundError(id, SemVersion.Parse("1.0.0").Value),
            (id, range) => catalog.GetCapability(id, SemVersion.Parse("1.0.0").Value));

        resolved.IsSuccess.ShouldBeTrue();
        resolved.Value.Capabilities[CapabilityId.FromTrusted("health")].Version.ShouldBe(SemVersion.Parse("1.0.0").Value);
    }

    [Fact]
    public void MOD_U_005_Excludes_remove_commands_and_enum_values()
    {
        var (catalog, _) = CreateCatalog();
        var doorType = catalog.Get(new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value)).Value;

        var customDoc = new DeviceTypeDocument(
            TypeId.FromTrusted("vendor.door"),
            SemVersion.Parse("1.0.0").Value,
            TypeRole.Device,
            Category.AccessControl,
            new TypeReference(doorType.Ref.Id, SemVersionRange.Parse("*").Value),
            [
                new CapabilityUse(
                    CapabilityId.FromTrusted("doorMode"),
                    SemVersionRange.Parse("^1.0").Value,
                    Exclude: new CapabilityExclude(
                        Commands: ["setMode"],
                        EnumValues: new Dictionary<string, IReadOnlyList<string>> { ["mode"] = ["emergencyClose"] }))
            ],
            null,
            [],
            LicenceClass.Full,
            doorType.Display,
            null,
            null,
            null);

        var resolved = TypeResolver.Resolve(
            customDoc,
            "vendor",
            (id, range) => ToDoc(doorType),
            (id, range) => catalog.GetCapability(id, SemVersion.Parse("1.0.0").Value));

        resolved.IsSuccess.ShouldBeTrue();
        var def = resolved.Value;
        def.Commands.ContainsKey(CommandPath.FromTrusted("doorMode.setMode")).ShouldBeFalse();
        var modeProp = def.Properties[PropertyPath.FromTrusted("doorMode.mode")];
        modeProp.EnumValues.ShouldNotContain(e => e.Code == "emergencyClose");
    }

    [Fact]
    public void MOD_U_006_Exclude_default_value_rejected()
    {
        var capDef = new CapabilityDefinition(
            CapabilityId.FromTrusted("customCap"),
            SemVersion.Parse("1.0.0").Value,
            new Dictionary<MemberId, PropertyDefinition>
            {
                [MemberId.FromTrusted("state")] = new(
                    PropertyPath.FromTrusted("customCap.state"),
                    "enum",
                    [new EnumValueDefinition("standardMode", Severity.Normal), new EnumValueDefinition("otherMode", Severity.Warning)],
                    Unit: null, Range: null, Decimals: null, MaxLength: null, ItemType: null, ObjectSchema: null, Writable: false,
                    History: new HistoryPolicy("all"), StaleAfter: null, Default: WpValue.Enum("standardMode"), BooleanSeverity: null)
            },
            new Dictionary<MemberId, CommandDefinition>(),
            new Dictionary<MemberId, EventDefinition>());

        var customDoc = new DeviceTypeDocument(
            TypeId.FromTrusted("vendor.test"),
            SemVersion.Parse("1.0.0").Value,
            TypeRole.Device,
            Category.Generic,
            null,
            [
                new CapabilityUse(
                    CapabilityId.FromTrusted("customCap"),
                    SemVersionRange.Parse("*").Value,
                    Exclude: new CapabilityExclude(
                        EnumValues: new Dictionary<string, IReadOnlyList<string>> { ["state"] = ["standardMode"] }))
            ],
            null,
            [],
            LicenceClass.Free,
            new TypeDisplay("i", "s", "p"),
            null,
            null,
            null);

        var resolved = TypeResolver.Resolve(
            customDoc,
            "vendor",
            (id, range) => ModelErrors.TypeNotFoundError(id, SemVersion.Parse("1.0.0").Value),
            (id, range) => capDef);

        resolved.IsSuccess.ShouldBeFalse();
        resolved.Error.Code.Value.ShouldBe(ModelErrors.ExcludeDefault.Value);
    }

    [Fact]
    public void MOD_U_007_Attributes_merge_with_override()
    {
        var parentAttrJson = "{\"type\":\"object\",\"properties\":{\"heldOpenAfter\":{\"type\":\"integer\",\"default\":60}}}";
        var childAttrJson = "{\"type\":\"object\",\"properties\":{\"heldOpenAfter\":{\"type\":\"integer\",\"default\":30}}}";

        var parentDoc = new DeviceTypeDocument(
            TypeId.FromTrusted("vendor.parent"),
            SemVersion.Parse("1.0.0").Value,
            TypeRole.Device,
            Category.Generic,
            null,
            [],
            new ParameterSchema(JsonDocument.Parse(parentAttrJson).RootElement),
            [],
            LicenceClass.Free,
            new TypeDisplay("i", "s", "p"),
            null,
            null,
            null);

        var childDoc = new DeviceTypeDocument(
            TypeId.FromTrusted("vendor.child"),
            SemVersion.Parse("1.0.0").Value,
            TypeRole.Device,
            Category.Generic,
            new TypeReference(parentDoc.Id, SemVersionRange.Parse("*").Value),
            [],
            new ParameterSchema(JsonDocument.Parse(childAttrJson).RootElement),
            [],
            LicenceClass.Free,
            new TypeDisplay("i", "s", "p"),
            null,
            null,
            null);

        var resolved = TypeResolver.Resolve(
            childDoc,
            "vendor",
            (id, range) => parentDoc,
            (id, range) => ModelErrors.CapabilityNotFoundError(id, SemVersion.Parse("1.0.0").Value));

        resolved.IsSuccess.ShouldBeTrue();
        var validator = new ValueValidator();
        var validated = validator.ValidateAttributes(resolved.Value, JsonDocument.Parse("{}").RootElement);
        validated.IsSuccess.ShouldBeTrue();
        validated.Value["heldOpenAfter"].AsInt().ShouldBe(30);
    }

    [Fact]
    public void MOD_U_008_Display_replaced_overrides_concatenated()
    {
        var parentDoc = new DeviceTypeDocument(
            TypeId.FromTrusted("vendor.parent"),
            SemVersion.Parse("1.0.0").Value,
            TypeRole.Device,
            Category.Generic,
            null,
            [],
            null,
            [],
            LicenceClass.Free,
            new TypeDisplay("parentIcon", "parentSymbol", "parentProp"),
            new StateSummaryRules([new StateSummaryOverride("cap.propA", [JsonDocument.Parse("\"a\"").RootElement], "Alarm")]),
            null,
            null);

        var childDoc = new DeviceTypeDocument(
            TypeId.FromTrusted("vendor.child"),
            SemVersion.Parse("1.0.0").Value,
            TypeRole.Device,
            Category.Generic,
            new TypeReference(parentDoc.Id, SemVersionRange.Parse("*").Value),
            [],
            null,
            [],
            LicenceClass.Free,
            new TypeDisplay("childIcon", "childSymbol", "childProp"),
            new StateSummaryRules([new StateSummaryOverride("cap.propB", [JsonDocument.Parse("\"b\"").RootElement], "Warning")]),
            null,
            null);

        var resolved = TypeResolver.Resolve(
            childDoc,
            "vendor",
            (id, range) => parentDoc,
            (id, range) => ModelErrors.CapabilityNotFoundError(id, SemVersion.Parse("1.0.0").Value));

        resolved.IsSuccess.ShouldBeTrue();
        var def = resolved.Value;
        def.Display.Icon.ShouldBe("childIcon");
        def.StateOverrides.Count.ShouldBe(2);
        def.StateOverrides[0].Property.Value.ShouldBe("cap.propB");
        def.StateOverrides[1].Property.Value.ShouldBe("cap.propA");
    }

    [Fact]
    public void MOD_U_009_IsA_via_lineage()
    {
        var parentDoc = new DeviceTypeDocument(
            TypeId.FromTrusted("ac.door"),
            SemVersion.Parse("1.0.0").Value,
            TypeRole.Device,
            Category.AccessControl,
            null,
            [],
            null,
            [],
            LicenceClass.Full,
            new TypeDisplay("i", "s", "p"),
            null,
            null,
            null);

        var childDoc = new DeviceTypeDocument(
            TypeId.FromTrusted("vendor.child.door"),
            SemVersion.Parse("1.0.0").Value,
            TypeRole.Device,
            Category.AccessControl,
            new TypeReference(parentDoc.Id, SemVersionRange.Parse("*").Value),
            [],
            null,
            [],
            LicenceClass.Full,
            new TypeDisplay("i", "s", "p"),
            null,
            null,
            null);

        var resolved = TypeResolver.Resolve(
            childDoc,
            "vendor",
            (id, range) => parentDoc,
            (id, range) => ModelErrors.CapabilityNotFoundError(id, SemVersion.Parse("1.0.0").Value));

        resolved.IsSuccess.ShouldBeTrue();
        resolved.Value.IsA(TypeId.FromTrusted("ac.door")).ShouldBeTrue();
        resolved.Value.IsA(TypeId.FromTrusted("vendor.child.door")).ShouldBeTrue();
        resolved.Value.IsA(TypeId.FromTrusted("generic.server")).ShouldBeFalse();
    }

    [Fact]
    public async Task MOD_U_010_GetLatest_per_major()
    {
        var (catalog, _) = CreateCatalog();
        var id = TypeId.FromTrusted("vendor.sample");
        var pkg = PackageId.FromTrusted("vendor.sample");

        DeviceTypeDocument MakeDoc(string ver) => new(
            id,
            SemVersion.Parse(ver).Value,
            TypeRole.Device,
            Category.Generic,
            null,
            [],
            null,
            [],
            LicenceClass.Free,
            new TypeDisplay("i", "s", "p"),
            null,
            null,
            null);

        await catalog.RegisterPackageAsync(pkg, SemVersion.Parse("1.0.0").Value, new PackageTypeDocuments([], [MakeDoc("1.0.0"), MakeDoc("1.2.0"), MakeDoc("2.0.0")], new Dictionary<string, JsonDocument>(), new Dictionary<string, ReadOnlyMemory<byte>>()), CancellationToken.None);

        var latest1 = catalog.GetLatest(id, 1);
        latest1.IsSuccess.ShouldBeTrue();
        latest1.Value.Ref.Version.ShouldBe(SemVersion.Parse("1.2.0").Value);

        var latest2 = catalog.GetLatest(id, 2);
        latest2.IsSuccess.ShouldBeTrue();
        latest2.Value.Ref.Version.ShouldBe(SemVersion.Parse("2.0.0").Value);
    }

    [Fact]
    public async Task MOD_U_011_Registration_swaps_snapshot_atomically()
    {
        var (catalog, _) = CreateCatalog();
        var oldRev = catalog.Revision;
        var eventCount = 0;
        catalog.Changed += (_, _) => eventCount++;

        var doc = new DeviceTypeDocument(
            TypeId.FromTrusted("vendor.test.device"),
            SemVersion.Parse("1.0.0").Value,
            TypeRole.Device,
            Category.Generic,
            null,
            [],
            null,
            [],
            LicenceClass.Free,
            new TypeDisplay("i", "s", "p"),
            null,
            null,
            null);

        var result = await catalog.RegisterPackageAsync(
            PackageId.FromTrusted("vendor.test"),
            SemVersion.Parse("1.0.0").Value,
            new PackageTypeDocuments([], [doc], new Dictionary<string, JsonDocument>(), new Dictionary<string, ReadOnlyMemory<byte>>()),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        catalog.Revision.ShouldBe(oldRev + 1);
        eventCount.ShouldBe(1);
    }

    [Fact]
    public async Task MOD_U_012_Reserved_namespace_enforced()
    {
        var (catalog, _) = CreateCatalog();
        var doc = new DeviceTypeDocument(
            TypeId.FromTrusted("ac.mydoor"),
            SemVersion.Parse("1.0.0").Value,
            TypeRole.Device,
            Category.AccessControl,
            null,
            [],
            null,
            [],
            LicenceClass.Free,
            new TypeDisplay("i", "s", "p"),
            null,
            null,
            null);

        var result = await catalog.RegisterPackageAsync(
            PackageId.FromTrusted("vendor.test"),
            SemVersion.Parse("1.0.0").Value,
            new PackageTypeDocuments([], [doc], new Dictionary<string, JsonDocument>(), new Dictionary<string, ReadOnlyMemory<byte>>()),
            CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.Value.ShouldBe(ModelErrors.ReservedNamespace.Value);
    }

    [Fact]
    public async Task MOD_U_013_Conflict_detection()
    {
        var (catalog, _) = CreateCatalog();
        var id = TypeId.FromTrusted("vendor.conflict");
        var pkg = PackageId.FromTrusted("vendor.conflict");

        DeviceTypeDocument MakeDoc(string icon) => new(
            id,
            SemVersion.Parse("1.0.0").Value,
            TypeRole.Device,
            Category.Generic,
            null,
            [],
            null,
            [],
            LicenceClass.Free,
            new TypeDisplay(icon, "s", "p"),
            null,
            null,
            null);

        await catalog.RegisterPackageAsync(
            pkg,
            SemVersion.Parse("1.0.0").Value,
            new PackageTypeDocuments([], [MakeDoc("icon1")], new Dictionary<string, JsonDocument>(), new Dictionary<string, ReadOnlyMemory<byte>>()),
            CancellationToken.None);

        var conflictRes = await catalog.RegisterPackageAsync(
            pkg,
            SemVersion.Parse("1.0.0").Value,
            new PackageTypeDocuments([], [MakeDoc("icon2")], new Dictionary<string, JsonDocument>(), new Dictionary<string, ReadOnlyMemory<byte>>()),
            CancellationToken.None);

        conflictRes.IsSuccess.ShouldBeFalse();
        conflictRes.Error.Code.Value.ShouldBe(ModelErrors.DocumentConflict.Value);
    }

    [Fact]
    public async Task MOD_U_014_Idempotent_re_registration()
    {
        var (catalog, _) = CreateCatalog();
        var id = TypeId.FromTrusted("vendor.idem");
        var pkg = PackageId.FromTrusted("vendor.idem");

        var doc = new DeviceTypeDocument(
            id,
            SemVersion.Parse("1.0.0").Value,
            TypeRole.Device,
            Category.Generic,
            null,
            [],
            null,
            [],
            LicenceClass.Free,
            new TypeDisplay("i", "s", "p"),
            null,
            null,
            null);

        var pkgDocs = new PackageTypeDocuments([], [doc], new Dictionary<string, JsonDocument>(), new Dictionary<string, ReadOnlyMemory<byte>>());
        await catalog.RegisterPackageAsync(pkg, SemVersion.Parse("1.0.0").Value, pkgDocs, CancellationToken.None);

        var revBefore = catalog.Revision;
        var eventCount = 0;
        catalog.Changed += (_, _) => eventCount++;

        var second = await catalog.RegisterPackageAsync(pkg, SemVersion.Parse("1.0.0").Value, pkgDocs, CancellationToken.None);
        second.IsSuccess.ShouldBeTrue();
        second.Value.Added.Count.ShouldBe(0);
        catalog.Revision.ShouldBe(revBefore);
        eventCount.ShouldBe(0);
    }

    [Fact]
    public async Task MOD_U_015_Unregister_keeps_type_resolvable()
    {
        var (catalog, _) = CreateCatalog();
        var id = TypeId.FromTrusted("vendor.unreg");
        var pkg = PackageId.FromTrusted("vendor.unreg");

        var doc = new DeviceTypeDocument(
            id,
            SemVersion.Parse("1.0.0").Value,
            TypeRole.Device,
            Category.Generic,
            null,
            [],
            null,
            [],
            LicenceClass.Free,
            new TypeDisplay("i", "s", "p"),
            null,
            null,
            null);

        var typeRef = new TypeRef(id, SemVersion.Parse("1.0.0").Value);
        await catalog.RegisterPackageAsync(pkg, SemVersion.Parse("1.0.0").Value, new PackageTypeDocuments([], [doc], new Dictionary<string, JsonDocument>(), new Dictionary<string, ReadOnlyMemory<byte>>()), CancellationToken.None);

        catalog.Get(typeRef).IsSuccess.ShouldBeTrue();

        await catalog.UnregisterPackageAsync(pkg, SemVersion.Parse("1.0.0").Value, CancellationToken.None);
        catalog.List(new TypeQuery(Source: pkg.Value)).ShouldBeEmpty();
    }

    [Fact]
    public void MOD_U_016_Validate_boolean_integer_decimal()
    {
        var validator = new ValueValidator();
        var boolProp = CreateProperty("b", "boolean");
        var intProp = CreateProperty("i", "integer", range: new NumericRange(1, 10));
        var decProp = CreateProperty("d", "decimal", range: new NumericRange(0.5, 99.5));

        validator.ValidateProperty(boolProp, WpValue.Bool(true)).IsSuccess.ShouldBeTrue();
        validator.ValidateProperty(boolProp, WpValue.Int(1)).IsSuccess.ShouldBeFalse();

        validator.ValidateProperty(intProp, WpValue.Int(5)).IsSuccess.ShouldBeTrue();
        validator.ValidateProperty(intProp, WpValue.Int(0)).IsSuccess.ShouldBeFalse();
        validator.ValidateProperty(intProp, WpValue.Int(15)).IsSuccess.ShouldBeFalse();

        validator.ValidateProperty(decProp, WpValue.Decimal(50.5m)).IsSuccess.ShouldBeTrue();
        validator.ValidateProperty(decProp, WpValue.Decimal(0.1m)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void MOD_U_017_Integer_accepts_integral_decimal()
    {
        var validator = new ValueValidator();
        var intProp = CreateProperty("i", "integer");

        var okRes = validator.ValidateProperty(intProp, WpValue.Decimal(5.0m));
        okRes.IsSuccess.ShouldBeTrue();
        okRes.Value.Kind.ShouldBe(WpValueKind.Int);
        okRes.Value.AsInt().ShouldBe(5);

        var failRes = validator.ValidateProperty(intProp, WpValue.Decimal(5.5m));
        failRes.IsSuccess.ShouldBeFalse();
        failRes.Error.Code.Value.ShouldBe(ModelErrors.TypeMismatch.Value);
    }

    [Fact]
    public void MOD_U_018_Enum_from_string()
    {
        var validator = new ValueValidator();
        var enumProp = CreateProperty("e", "enum", enumValues: [new EnumValueDefinition("open", Severity.Normal), new EnumValueDefinition("closed", Severity.Normal)]);

        var res = validator.ValidateProperty(enumProp, WpValue.String("open"));
        res.IsSuccess.ShouldBeTrue();
        res.Value.Kind.ShouldBe(WpValueKind.Enum);
        res.Value.AsEnum().ShouldBe("open");

        var fail = validator.ValidateProperty(enumProp, WpValue.String("opened"));
        fail.IsSuccess.ShouldBeFalse();
        fail.Error.Code.Value.ShouldBe(ModelErrors.EnumUnknown.Value);
    }

    [Fact]
    public void MOD_U_019_String_NFC_and_max_length()
    {
        var validator = new ValueValidator();
        var strProp = CreateProperty("s", "string", maxLength: 5);

        var decomposed = "e\u0301";
        var res = validator.ValidateProperty(strProp, WpValue.String(decomposed));
        res.IsSuccess.ShouldBeTrue();
        res.Value.AsString().IsNormalized(System.Text.NormalizationForm.FormC).ShouldBeTrue();

        var tooLong = validator.ValidateProperty(strProp, WpValue.String("123456"));
        tooLong.IsSuccess.ShouldBeFalse();
        tooLong.Error.Code.Value.ShouldBe(ModelErrors.TooLong.Value);
    }

    [Fact]
    public void MOD_U_020_List_validation()
    {
        var validator = new ValueValidator();
        var listProp = CreateProperty("l", "list", itemType: "integer");

        var valid = validator.ValidateProperty(listProp, WpValue.List([WpValue.Int(1), WpValue.Int(2)]));
        valid.IsSuccess.ShouldBeTrue();

        var invalid = validator.ValidateProperty(listProp, WpValue.List([WpValue.Int(1), WpValue.Int(2), WpValue.String("bad")]));
        invalid.IsSuccess.ShouldBeFalse();

        var overMax = validator.ValidateProperty(listProp, WpValue.List(Enumerable.Range(0, 1001).Select(i => WpValue.Int(i)).ToList()));
        overMax.IsSuccess.ShouldBeFalse();
        overMax.Error.Code.Value.ShouldBe(ModelErrors.TooLong.Value);
    }

    [Fact]
    public void MOD_U_021_Object_validation_against_schema()
    {
        var validator = new ValueValidator();
        var schemaJson = "{\"type\":\"object\",\"properties\":{\"requiredField\":{\"type\":\"string\"}},\"required\":[\"requiredField\"]}";
        var objProp = CreateProperty("o", "object", objectSchema: new ParameterSchema(JsonDocument.Parse(schemaJson).RootElement));

        var valid = validator.ValidateProperty(objProp, WpValue.Object(JsonDocument.Parse("{\"requiredField\":\"ok\"}").RootElement));
        valid.IsSuccess.ShouldBeTrue();

        var invalid = validator.ValidateProperty(objProp, WpValue.Object(JsonDocument.Parse("{}").RootElement));
        invalid.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void MOD_U_022_Unit_conversion()
    {
        var validator = new ValueValidator();
        var celProp = CreateProperty("temp", "decimal", unit: "Cel");
        var energyProp = CreateProperty("energy", "decimal", unit: "W.h");

        var tempRes = validator.ValidateProperty(celProp, WpValue.Decimal(77), reportedUnit: "[degF]");
        tempRes.IsSuccess.ShouldBeTrue();
        tempRes.Value.AsDecimal().ShouldBe(25);

        var energyRes = validator.ValidateProperty(energyProp, WpValue.Decimal(1), reportedUnit: "kW.h");
        energyRes.IsSuccess.ShouldBeTrue();
        energyRes.Value.AsDecimal().ShouldBe(1000);
    }

    [Fact]
    public void MOD_U_023_Incompatible_unit()
    {
        var validator = new ValueValidator();
        var celProp = CreateProperty("temp", "decimal", unit: "Cel");

        var res = validator.ValidateProperty(celProp, WpValue.Decimal(25), reportedUnit: "m");
        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe(ModelErrors.UnitIncompatible.Value);
    }

    [Fact]
    public void MOD_U_024_Command_arguments_defaults_applied()
    {
        var (catalog, _) = CreateCatalog();
        var openCmd = catalog.GetCapability(CapabilityId.FromTrusted("openable"), SemVersion.Parse("1.0.0").Value).Value.Commands[MemberId.FromTrusted("open")];
        var validator = new ValueValidator();

        var res = validator.ValidateArguments(openCmd, JsonDocument.Parse("{}").RootElement);
        res.IsSuccess.ShouldBeTrue();
        res.Value.ShouldContainKey("duration");
        res.Value["duration"].AsDuration().ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void MOD_U_025_Command_argument_invalid()
    {
        var (catalog, _) = CreateCatalog();
        var openCmd = catalog.GetCapability(CapabilityId.FromTrusted("openable"), SemVersion.Parse("1.0.0").Value).Value.Commands[MemberId.FromTrusted("open")];
        var validator = new ValueValidator();

        var res = validator.ValidateArguments(openCmd, JsonDocument.Parse("{\"duration\":\"PT2H\"}").RootElement);
        res.IsSuccess.ShouldBeFalse();
        res.Error.FieldErrors.ShouldContain(f => f.Code == ModelErrors.OutOfRange);
    }

    [Fact]
    public void MOD_U_026_Risk_by_argument()
    {
        var (catalog, _) = CreateCatalog();
        var doorModeCap = catalog.GetCapability(CapabilityId.FromTrusted("doorMode"), SemVersion.Parse("1.0.0").Value).Value;
        var setModeCmd = doorModeCap.Commands[MemberId.FromTrusted("setMode")];

        var criticalRisk = setModeCmd.EffectiveRisk(new Dictionary<string, WpValue> { ["mode"] = WpValue.Enum("lockedDown") });
        criticalRisk.ShouldBe(Risk.Critical);
    }

    [Fact]
    public void MOD_U_027_Event_payload_validation()
    {
        var (catalog, _) = CreateCatalog();
        var accessEvents = catalog.GetCapability(CapabilityId.FromTrusted("accessEvents"), SemVersion.Parse("1.0.0").Value).Value;
        var deniedEvt = accessEvents.Events[MemberId.FromTrusted("denied")];
        var validator = new ValueValidator();

        var validPayload = new Dictionary<string, WpValue> { ["reason"] = WpValue.Enum("unknownCredential") };
        var validRes = validator.ValidatePayload(deniedEvt, validPayload);
        validRes.IsSuccess.ShouldBeTrue();

        var invalidPayload = new Dictionary<string, WpValue> { ["reason"] = WpValue.Enum("bogus") };
        var invalidRes = validator.ValidatePayload(deniedEvt, invalidPayload);
        invalidRes.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void MOD_U_028_State_offline_wins()
    {
        var (catalog, _) = CreateCatalog();
        var doorType = catalog.Get(new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value)).Value;
        var evaluator = new StateSummaryEvaluator();

        var view = new FakeStateView(new Dictionary<PropertyPath, PropertyValueView>
        {
            [PropertyPath.FromTrusted("health.connectivity")] = new(WpValue.Enum("offline"), Quality.Good),
            [PropertyPath.FromTrusted("doorPosition.position")] = new(WpValue.Enum("forced"), Quality.Good),
        });

        var summary = evaluator.Evaluate(doorType, view, new DeviceFlags(false, false), new Dictionary<string, WpValue>(), null);
        summary.State.ShouldBe(OverallState.Offline);
        summary.Cause?.Value.ShouldBe("health.connectivity");
    }

    [Fact]
    public void MOD_U_029_State_override_first()
    {
        var (catalog, _) = CreateCatalog();
        var doorType = catalog.Get(new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value)).Value;
        var evaluator = new StateSummaryEvaluator();

        var view = new FakeStateView(new Dictionary<PropertyPath, PropertyValueView>
        {
            [PropertyPath.FromTrusted("health.connectivity")] = new(WpValue.Enum("online"), Quality.Good),
            [PropertyPath.FromTrusted("lockable.lockState")] = new(WpValue.Enum("unlocked"), Quality.Good),
            [PropertyPath.FromTrusted("doorPosition.position")] = new(WpValue.Enum("closed"), Quality.Good),
        });

        var summary = evaluator.Evaluate(doorType, view, new DeviceFlags(false, false), new Dictionary<string, WpValue>(), null);
        summary.State.ShouldBe(OverallState.NormalActive);
    }

    [Fact]
    public void MOD_U_030_State_worst_severity()
    {
        var (catalog, _) = CreateCatalog();
        var doorType = catalog.Get(new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value)).Value;
        var evaluator = new StateSummaryEvaluator();

        var view = new FakeStateView(new Dictionary<PropertyPath, PropertyValueView>
        {
            [PropertyPath.FromTrusted("health.connectivity")] = new(WpValue.Enum("online"), Quality.Good),
            [PropertyPath.FromTrusted("doorPosition.position")] = new(WpValue.Enum("heldOpen"), Quality.Good),
            [PropertyPath.FromTrusted("tamper.tampered")] = new(WpValue.Bool(true), Quality.Good),
        });

        var summary = evaluator.Evaluate(doorType, view, new DeviceFlags(false, false), new Dictionary<string, WpValue>(), null);
        summary.State.ShouldBe(OverallState.Alarm);
        summary.Cause?.Value.ShouldBe("tamper.tampered");
    }

    [Fact]
    public void MOD_U_031_State_faults_to_Fault()
    {
        var (catalog, _) = CreateCatalog();
        var doorType = catalog.Get(new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value)).Value;
        var evaluator = new StateSummaryEvaluator();

        var view = new FakeStateView(new Dictionary<PropertyPath, PropertyValueView>
        {
            [PropertyPath.FromTrusted("health.connectivity")] = new(WpValue.Enum("online"), Quality.Good),
            [PropertyPath.FromTrusted("health.faults")] = new(WpValue.List([WpValue.Enum("batteryLow")]), Quality.Good),
        });

        var summary = evaluator.Evaluate(doorType, view, new DeviceFlags(false, false), new Dictionary<string, WpValue>(), null);
        summary.State.ShouldBe(OverallState.Fault);
        summary.Cause?.Value.ShouldBe("health.faults");
    }

    [Fact]
    public void MOD_U_032_State_measurement_thresholds_with_hysteresis()
    {
        var (catalog, _) = CreateCatalog();
        var analogType = catalog.Get(new TypeRef(TypeId.FromTrusted("generic.analog-input"), SemVersion.Parse("1.0.0").Value)).Value;
        var evaluator = new StateSummaryEvaluator();
        var prop = PropertyPath.FromTrusted("measurement.value");

        var attributes = new Dictionary<string, WpValue>
        {
            ["alarmAbove"] = WpValue.Decimal(30m),
            ["hysteresis"] = WpValue.Decimal(1m),
        };

        var view1 = new FakeStateView(new Dictionary<PropertyPath, PropertyValueView>
        {
            [PropertyPath.FromTrusted("health.connectivity")] = new(WpValue.Enum("online"), Quality.Good),
            [prop] = new(WpValue.Decimal(30.5m), Quality.Good),
        });
        var sum1 = evaluator.Evaluate(analogType, view1, new DeviceFlags(false, false), attributes, null);
        sum1.State.ShouldBe(OverallState.Alarm);

        var view2 = new FakeStateView(new Dictionary<PropertyPath, PropertyValueView>
        {
            [PropertyPath.FromTrusted("health.connectivity")] = new(WpValue.Enum("online"), Quality.Good),
            [prop] = new(WpValue.Decimal(29.5m), Quality.Good),
        });
        var sum2 = evaluator.Evaluate(analogType, view2, new DeviceFlags(false, false), attributes, sum1);
        sum2.State.ShouldBe(OverallState.Alarm);

        var view3 = new FakeStateView(new Dictionary<PropertyPath, PropertyValueView>
        {
            [PropertyPath.FromTrusted("health.connectivity")] = new(WpValue.Enum("online"), Quality.Good),
            [prop] = new(WpValue.Decimal(28.9m), Quality.Good),
        });
        var sum3 = evaluator.Evaluate(analogType, view3, new DeviceFlags(false, false), attributes, sum2);
        sum3.State.ShouldBe(OverallState.Normal);
    }

    [Fact]
    public void MOD_U_033_State_stale_and_bad_ignored_all_unknown_to_Unknown()
    {
        var (catalog, _) = CreateCatalog();
        var doorType = catalog.Get(new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value)).Value;
        var evaluator = new StateSummaryEvaluator();

        var view = new FakeStateView(new Dictionary<PropertyPath, PropertyValueView>
        {
            [PropertyPath.FromTrusted("health.connectivity")] = new(WpValue.Enum("online"), Quality.Stale),
            [PropertyPath.FromTrusted("doorPosition.position")] = new(WpValue.Enum("forced"), Quality.Bad),
        });

        var summary = evaluator.Evaluate(doorType, view, new DeviceFlags(false, false), new Dictionary<string, WpValue>(), null);
        summary.State.ShouldBe(OverallState.Unknown);
    }

    [Fact]
    public void MOD_U_034_State_disabled_and_maintenance_flags()
    {
        var (catalog, _) = CreateCatalog();
        var doorType = catalog.Get(new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value)).Value;
        var evaluator = new StateSummaryEvaluator();

        var view = new FakeStateView(new Dictionary<PropertyPath, PropertyValueView>
        {
            [PropertyPath.FromTrusted("health.connectivity")] = new(WpValue.Enum("online"), Quality.Good),
            [PropertyPath.FromTrusted("doorPosition.position")] = new(WpValue.Enum("forced"), Quality.Good),
        });

        var disabledSummary = evaluator.Evaluate(doorType, view, new DeviceFlags(true, false), new Dictionary<string, WpValue>(), null);
        disabledSummary.State.ShouldBe(OverallState.Disabled);
        disabledSummary.UnderlyingState.ShouldBe(OverallState.Alarm);

        var maintSummary = evaluator.Evaluate(doorType, view, new DeviceFlags(false, true), new Dictionary<string, WpValue>(), null);
        maintSummary.State.ShouldBe(OverallState.Maintenance);
        maintSummary.UnderlyingState.ShouldBe(OverallState.Alarm);
    }

    [Fact]
    public async Task MOD_U_035_Translation_bundle_merge_and_ETag()
    {
        var (catalog, _) = CreateCatalog();
        var initialBytes = catalog.Resources.GetTranslationBundle("en", out var initialEtag);
        initialBytes.Length.ShouldBeGreaterThan(0);
        initialEtag.ShouldStartWith("\"");

        var pkg = PackageId.FromTrusted("vendor.pkg");
        var pkgTrans = JsonDocument.Parse("{\"types\":{\"vendor.pkg.test\":{\"name\":\"Vendor Test\"}}}");
        var pkgDocs = new PackageTypeDocuments(
            [],
            [],
            new Dictionary<string, JsonDocument> { ["en"] = pkgTrans },
            new Dictionary<string, ReadOnlyMemory<byte>>());

        await catalog.RegisterPackageAsync(pkg, SemVersion.Parse("1.0.0").Value, pkgDocs, CancellationToken.None);

        var afterBytes = catalog.Resources.GetTranslationBundle("en", out var afterEtag);
        afterEtag.ShouldNotBe(initialEtag);
        var jsonText = System.Text.Encoding.UTF8.GetString(afterBytes.Span);
        jsonText.ShouldContain("Vendor Test");
    }

    private static PropertyDefinition CreateProperty(
        string name,
        string valueType,
        IReadOnlyList<EnumValueDefinition>? enumValues = null,
        string? unit = null,
        NumericRange? range = null,
        int? maxLength = null,
        string? itemType = null,
        ParameterSchema? objectSchema = null)
    {
        return new PropertyDefinition(
            PropertyPath.FromTrusted($"test.{name}"),
            valueType,
            enumValues ?? [],
            unit,
            range,
            Decimals: 2,
            MaxLength: maxLength,
            ItemType: itemType,
            ObjectSchema: objectSchema,
            Writable: false,
            History: new HistoryPolicy("all"),
            StaleAfter: null,
            Default: null,
            BooleanSeverity: null);
    }

    private static DeviceTypeDocument ToDoc(DeviceTypeDefinition def) =>
        new(
            def.Ref.Id,
            def.Ref.Version,
            def.Role,
            def.Category,
            null,
            def.Capabilities.Select(c => new CapabilityUse(c.Key, SemVersionRange.Parse("*").Value, c.Value.Optional)).ToList(),
            def.Attributes,
            def.AllowedChildTypes,
            def.LicenceClass,
            def.Display,
            null,
            def.DefaultAlarmRules,
            def.DefaultLinks);

    private sealed class FakeStateView(IReadOnlyDictionary<PropertyPath, PropertyValueView> values) : IDeviceStateView
    {
        public bool TryGet(PropertyPath path, out PropertyValueView value) => values.TryGetValue(path, out value);
    }
}
