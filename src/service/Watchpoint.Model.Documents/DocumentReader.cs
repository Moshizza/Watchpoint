using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using Json.Schema;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Model.Documents;

public sealed class DocumentReader : IDocumentReader
{
    private readonly JsonSchema _capabilitySchema;
    private readonly JsonSchema _deviceTypeSchema;
    private readonly JsonSchema _manifestSchema;
    private readonly JsonSchema _parameterSchema;

    private static readonly object _registryLock = new();
    private static bool _schemasRegistered;

    public DocumentReader(string schemasDirectory)
    {
        lock (_registryLock)
        {
            if (!_schemasRegistered)
            {
                _parameterSchema = JsonSchema.FromFile(Path.Combine(schemasDirectory, "parameter.schema.json"));
                _capabilitySchema = JsonSchema.FromFile(Path.Combine(schemasDirectory, "capability.schema.json"));
                _deviceTypeSchema = JsonSchema.FromFile(Path.Combine(schemasDirectory, "device-type.schema.json"));
                _manifestSchema = JsonSchema.FromFile(Path.Combine(schemasDirectory, "manifest.schema.json"));

                SchemaRegistry.Global.Register(_parameterSchema);
                SchemaRegistry.Global.Register(_capabilitySchema);
                SchemaRegistry.Global.Register(_deviceTypeSchema);
                SchemaRegistry.Global.Register(_manifestSchema);
                _schemasRegistered = true;
            }
            else
            {
                _parameterSchema = (JsonSchema)SchemaRegistry.Global.Get(new Uri("https://schemas.watchpoint.dev/parameter/1.json"))!;
                _capabilitySchema = (JsonSchema)SchemaRegistry.Global.Get(new Uri("https://schemas.watchpoint.dev/capability/1.json"))!;
                _deviceTypeSchema = (JsonSchema)SchemaRegistry.Global.Get(new Uri("https://schemas.watchpoint.dev/device-type/1.json"))!;
                _manifestSchema = (JsonSchema)SchemaRegistry.Global.Get(new Uri("https://schemas.watchpoint.dev/manifest/1.json"))!;
            }
        }
    }

    public DocumentReader(JsonSchema capabilitySchema, JsonSchema deviceTypeSchema, JsonSchema manifestSchema, JsonSchema parameterSchema)
    {
        _capabilitySchema = capabilitySchema;
        _deviceTypeSchema = deviceTypeSchema;
        _manifestSchema = manifestSchema;
        _parameterSchema = parameterSchema;

        SchemaRegistry.Global.Register(_parameterSchema);
        SchemaRegistry.Global.Register(_capabilitySchema);
        SchemaRegistry.Global.Register(_deviceTypeSchema);
        SchemaRegistry.Global.Register(_manifestSchema);
    }

    public Result<CapabilityDocument> ReadCapability(ReadOnlySpan<byte> utf8, string sourcePath)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(utf8);
        }
        catch (JsonException ex)
        {
            return Error.Validation("common.validation.failed", [CommonErrors.InvalidFormatAt(string.Empty, ex.Message)]);
        }

        if (node is null)
        {
            return Error.Validation("common.validation.failed", [CommonErrors.RequiredAt(string.Empty)]);
        }

        using var doc = JsonDocument.Parse(utf8.ToArray());
        var root = doc.RootElement;

        var validationResult = _capabilitySchema.Evaluate(root, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        if (!validationResult.IsValid)
        {
            return ConvertEvaluationErrors(validationResult);
        }

        var semanticErrors = new List<FieldError>();

        var propList = new List<PropertyDefinitionDoc>();
        if (root.TryGetProperty("properties", out var propsElem))
        {
            int propIndex = 0;
            foreach (var p in propsElem.EnumerateArray())
            {
                string propPath = $"/properties/{propIndex}";
                var idResult = MemberId.Parse(p.GetProperty("id").GetString()!);
                if (!idResult.IsSuccess)
                {
                    semanticErrors.Add(CommonErrors.InvalidFormatAt($"{propPath}/id", "MemberId"));
                }

                string valueType = p.GetProperty("valueType").GetString()!;

                // Enum values validation
                List<EnumValueDoc>? enumValues = null;
                if (p.TryGetProperty("enumValues", out var evArray))
                {
                    enumValues = [];
                    var seenCodes = new HashSet<string>();
                    int evIdx = 0;
                    foreach (var ev in evArray.EnumerateArray())
                    {
                        string code = ev.GetProperty("code").GetString()!;
                        string severity = ev.GetProperty("severity").GetString()!;
                        if (!seenCodes.Add(code))
                        {
                            semanticErrors.Add(CommonErrors.DuplicateAt($"{propPath}/enumValues/{evIdx}/code", code));
                        }
                        enumValues.Add(new EnumValueDoc(MemberId.FromTrusted(code), severity));
                        evIdx++;
                    }
                }

                // Range validation min <= max
                RangeDoc? range = null;
                if (p.TryGetProperty("range", out var rangeElem))
                {
                    double? min = rangeElem.TryGetProperty("min", out var minVal) ? minVal.GetDouble() : null;
                    double? max = rangeElem.TryGetProperty("max", out var maxVal) ? maxVal.GetDouble() : null;
                    if (min.HasValue && max.HasValue && min.Value > max.Value)
                    {
                        semanticErrors.Add(CommonErrors.OutOfRangeAt($"{propPath}/range/min", (long)min.Value, (long)max.Value));
                    }
                    range = new RangeDoc(min, max);
                }

                // staleAfter duration
                TimeSpan? staleAfter = null;
                if (p.TryGetProperty("staleAfter", out var staleProp))
                {
                    var durRes = ParseIsoDuration(staleProp.GetString()!);
                    if (!durRes.IsSuccess)
                    {
                        semanticErrors.Add(CommonErrors.InvalidFormatAt($"{propPath}/staleAfter", "ISO 8601 duration"));
                    }
                    else
                    {
                        staleAfter = durRes.Value;
                    }
                }

                // Object schema
                ParameterSchema? objectSchema = null;
                if (p.TryGetProperty("objectSchema", out var objSchemaElem))
                {
                    var schemaRes = ParameterSchema.Parse(objSchemaElem);
                    if (!schemaRes.IsSuccess)
                    {
                        semanticErrors.AddRange(schemaRes.Error.FieldErrors);
                    }
                    else
                    {
                        objectSchema = schemaRes.Value;
                    }
                }

                // History
                HistoryConfigDoc? history = null;
                if (p.TryGetProperty("history", out var histElem))
                {
                    string mode = histElem.GetProperty("mode").GetString()!;
                    string? type = histElem.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;
                    double? threshold = histElem.TryGetProperty("threshold", out var thProp) ? thProp.GetDouble() : null;
                    TimeSpan? interval = null;
                    if (histElem.TryGetProperty("interval", out var intProp))
                    {
                        var intRes = ParseIsoDuration(intProp.GetString()!);
                        if (!intRes.IsSuccess)
                        {
                            semanticErrors.Add(CommonErrors.InvalidFormatAt($"{propPath}/history/interval", "ISO 8601 duration"));
                        }
                        else
                        {
                            interval = intRes.Value;
                        }
                    }
                    history = new HistoryConfigDoc(mode, type, threshold, interval);
                }

                BooleanSeverityDoc? boolSev = null;
                if (p.TryGetProperty("booleanSeverity", out var bsElem))
                {
                    boolSev = new BooleanSeverityDoc(bsElem.GetProperty("true").GetString()!, bsElem.GetProperty("false").GetString()!);
                }

                propList.Add(new PropertyDefinitionDoc(
                    idResult.IsSuccess ? idResult.Value : MemberId.FromTrusted("dummy"),
                    valueType,
                    enumValues,
                    p.TryGetProperty("unit", out var u) ? u.GetString() : null,
                    range,
                    p.TryGetProperty("decimals", out var d) ? d.GetInt32() : null,
                    p.TryGetProperty("maxLength", out var ml) ? ml.GetInt32() : null,
                    p.TryGetProperty("itemType", out var it) ? it.GetString() : null,
                    objectSchema,
                    p.TryGetProperty("writable", out var w) && w.GetBoolean(),
                    history,
                    staleAfter,
                    p.TryGetProperty("default", out var def) ? def.Clone() : null,
                    boolSev));

                propIndex++;
            }
        }

        var cmdList = new List<CommandDefinitionDoc>();
        if (root.TryGetProperty("commands", out var cmdsElem))
        {
            int cmdIdx = 0;
            foreach (var c in cmdsElem.EnumerateArray())
            {
                string cmdPath = $"/commands/{cmdIdx}";
                var idResult = MemberId.Parse(c.GetProperty("id").GetString()!);
                if (!idResult.IsSuccess)
                {
                    semanticErrors.Add(CommonErrors.InvalidFormatAt($"{cmdPath}/id", "MemberId"));
                }

                ParameterSchema paramSchema;
                if (c.TryGetProperty("parameters", out var pElem))
                {
                    var pRes = ParameterSchema.Parse(pElem);
                    if (!pRes.IsSuccess)
                    {
                        semanticErrors.AddRange(pRes.Error.FieldErrors);
                        paramSchema = new ParameterSchema(pElem);
                    }
                    else
                    {
                        paramSchema = pRes.Value;
                    }
                }
                else
                {
                    using var emptyObj = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
                    paramSchema = new ParameterSchema(emptyObj.RootElement);
                }

                TimeSpan? timeout = null;
                if (c.TryGetProperty("timeout", out var toElem))
                {
                    var durRes = ParseIsoDuration(toElem.GetString()!);
                    if (!durRes.IsSuccess)
                    {
                        semanticErrors.Add(CommonErrors.InvalidFormatAt($"{cmdPath}/timeout", "ISO 8601 duration"));
                    }
                    else
                    {
                        timeout = durRes.Value;
                        if (timeout.Value > TimeSpan.FromMinutes(10))
                        {
                            semanticErrors.Add(CommonErrors.OutOfRangeAt($"{cmdPath}/timeout", 0, 600));
                        }
                    }
                }

                ExpectedEffectDoc? expectedEffect = null;
                if (c.TryGetProperty("expectedEffect", out var eeElem))
                {
                    string eeProp = eeElem.GetProperty("property").GetString()!;
                    TimeSpan? within = null;
                    if (eeElem.TryGetProperty("within", out var wElem))
                    {
                        var wRes = ParseIsoDuration(wElem.GetString()!);
                        if (wRes.IsSuccess) within = wRes.Value;
                    }
                    expectedEffect = new ExpectedEffectDoc(
                        eeProp,
                        eeElem.TryGetProperty("equals", out var eq) ? eq.Clone() : null,
                        eeElem.TryGetProperty("in", out var inArr) ? inArr.EnumerateArray().Select(x => x.Clone()).ToList() : null,
                        within);
                }

                cmdList.Add(new CommandDefinitionDoc(
                    idResult.IsSuccess ? idResult.Value : MemberId.FromTrusted("dummy"),
                    paramSchema,
                    c.TryGetProperty("risk", out var rElem) ? rElem.GetString()! : "normal",
                    timeout,
                    c.TryGetProperty("idempotent", out var idemp) && idemp.GetBoolean(),
                    c.TryGetProperty("batchable", out var batch) && batch.GetBoolean(),
                    null,
                    expectedEffect));

                cmdIdx++;
            }
        }

        var evtList = new List<EventDefinitionDoc>();
        if (root.TryGetProperty("events", out var evtsElem))
        {
            int evtIdx = 0;
            foreach (var e in evtsElem.EnumerateArray())
            {
                string evtPath = $"/events/{evtIdx}";
                var idResult = MemberId.Parse(e.GetProperty("id").GetString()!);
                if (!idResult.IsSuccess)
                {
                    semanticErrors.Add(CommonErrors.InvalidFormatAt($"{evtPath}/id", "MemberId"));
                }

                ParameterSchema? payloadSchema = null;
                if (e.TryGetProperty("payload", out var plElem))
                {
                    var plRes = ParameterSchema.Parse(plElem);
                    if (!plRes.IsSuccess)
                    {
                        semanticErrors.AddRange(plRes.Error.FieldErrors);
                    }
                    else
                    {
                        payloadSchema = plRes.Value;
                    }
                }

                evtList.Add(new EventDefinitionDoc(
                    idResult.IsSuccess ? idResult.Value : MemberId.FromTrusted("dummy"),
                    e.TryGetProperty("severity", out var sev) ? sev.GetString()! : "info",
                    payloadSchema,
                    e.TryGetProperty("personalData", out var pd) ? pd.EnumerateArray().Select(x => x.GetString()!).ToList() : null));

                evtIdx++;
            }
        }

        if (semanticErrors.Count > 0)
        {
            return Error.Validation("common.validation.failed", semanticErrors);
        }

        var capId = CapabilityId.Parse(root.GetProperty("id").GetString()!).Value;
        var semVer = SemVersion.Parse(root.GetProperty("version").GetString()!).Value;

        return new CapabilityDocument(capId, semVer, propList, cmdList, evtList);
    }

    public Result<DeviceTypeDocument> ReadDeviceType(ReadOnlySpan<byte> utf8, string sourcePath)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(utf8);
        }
        catch (JsonException ex)
        {
            return Error.Validation("common.validation.failed", [CommonErrors.InvalidFormatAt(string.Empty, ex.Message)]);
        }

        if (node is null)
        {
            return Error.Validation("common.validation.failed", [CommonErrors.RequiredAt(string.Empty)]);
        }

        using var doc = JsonDocument.Parse(utf8.ToArray());
        var root = doc.RootElement;

        var validationResult = _deviceTypeSchema.Evaluate(root, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        if (!validationResult.IsValid)
        {
            return ConvertEvaluationErrors(validationResult);
        }

        var semanticErrors = new List<FieldError>();

        var idRes = TypeId.Parse(root.GetProperty("id").GetString()!);
        if (!idRes.IsSuccess)
        {
            semanticErrors.Add(CommonErrors.InvalidFormatAt("/id", "TypeId"));
        }

        var verRes = SemVersion.Parse(root.GetProperty("version").GetString()!);
        if (!verRes.IsSuccess)
        {
            semanticErrors.Add(CommonErrors.InvalidFormatAt("/version", "SemVersion"));
        }

        string roleStr = root.GetProperty("role").GetString()!;
        if (!Enum.TryParse<TypeRole>(roleStr, ignoreCase: true, out var role))
        {
            semanticErrors.Add(CommonErrors.NotAllowedAt("/role", ["device", "server"]));
        }

        string catStr = root.GetProperty("category").GetString()!;
        if (!Enum.TryParse<Category>(catStr, ignoreCase: true, out var category))
        {
            semanticErrors.Add(CommonErrors.NotAllowedAt("/category", Enum.GetNames<Category>()));
        }

        TypeReference? extends = null;
        if (root.TryGetProperty("extends", out var extElem) && extElem.ValueKind == JsonValueKind.Object)
        {
            var extId = TypeId.Parse(extElem.GetProperty("id").GetString()!).Value;
            var extRange = SemVersionRange.Parse(extElem.GetProperty("version").GetString()!).Value;
            extends = new TypeReference(extId, extRange);
        }

        var caps = new List<CapabilityUse>();
        int capIdx = 0;
        foreach (var c in root.GetProperty("capabilities").EnumerateArray())
        {
            var capId = CapabilityId.Parse(c.GetProperty("id").GetString()!).Value;
            var capRange = SemVersionRange.Parse(c.GetProperty("version").GetString()!).Value;
            bool opt = c.TryGetProperty("optional", out var o) && o.GetBoolean();

            CapabilityExclude? exclude = null;
            if (c.TryGetProperty("exclude", out var exElem))
            {
                var cmdEx = exElem.TryGetProperty("commands", out var ce) ? ce.EnumerateArray().Select(x => x.GetString()!).ToList() : null;
                var evtEx = exElem.TryGetProperty("events", out var ee) ? ee.EnumerateArray().Select(x => x.GetString()!).ToList() : null;
                var valEx = new Dictionary<string, IReadOnlyList<string>>();
                if (exElem.TryGetProperty("enumValues", out var ev))
                {
                    foreach (var prop in ev.EnumerateObject())
                    {
                        valEx[prop.Name] = prop.Value.EnumerateArray().Select(x => x.GetString()!).ToList();
                    }
                }
                exclude = new CapabilityExclude(cmdEx, evtEx, valEx);
            }

            caps.Add(new CapabilityUse(capId, capRange, opt, exclude));
            capIdx++;
        }

        ParameterSchema? attributes = null;
        if (root.TryGetProperty("attributes", out var attrElem))
        {
            var attrRes = ParameterSchema.Parse(attrElem);
            if (!attrRes.IsSuccess)
            {
                semanticErrors.AddRange(attrRes.Error.FieldErrors);
            }
            else
            {
                attributes = attrRes.Value;
            }
        }

        string licStr = root.GetProperty("licenceClass").GetString()!;
        Enum.TryParse<LicenceClass>(licStr, ignoreCase: true, out var licenceClass);

        var dispElem = root.GetProperty("display");
        var display = new TypeDisplay(
            dispElem.GetProperty("icon").GetString()!,
            dispElem.GetProperty("symbol").GetString()!,
            dispElem.GetProperty("primaryProperty").GetString()!);

        StateSummaryRules? stateSummary = null;
        if (root.TryGetProperty("stateSummary", out var ssElem) && ssElem.TryGetProperty("overrides", out var ovElem))
        {
            var ovList = new List<StateSummaryOverride>();
            foreach (var ov in ovElem.EnumerateArray())
            {
                ovList.Add(new StateSummaryOverride(
                    ov.GetProperty("property").GetString()!,
                    ov.GetProperty("values").EnumerateArray().Select(x => x.Clone()).ToList(),
                    ov.GetProperty("state").GetString()!));
            }
            stateSummary = new StateSummaryRules(ovList);
        }

        var childTypes = root.TryGetProperty("allowedChildTypes", out var actElem)
            ? actElem.EnumerateArray().Select(x => x.GetString()!).ToList()
            : [];

        var alarmRules = root.TryGetProperty("defaultAlarmRules", out var arElem)
            ? arElem.EnumerateArray().Select(x => x.GetString()!).ToList()
            : [];

        var defaultLinks = new List<DefaultLink>();
        if (root.TryGetProperty("defaultLinks", out var dlElem))
        {
            foreach (var dl in dlElem.EnumerateArray())
            {
                string kind = dl.GetProperty("kind").GetString()!;
                Category? pCat = dl.TryGetProperty("peerCategory", out var pc) && Enum.TryParse<Category>(pc.GetString(), ignoreCase: true, out var catVal)
                    ? catVal : null;
                TypeId? pType = null;
                if (dl.TryGetProperty("peerType", out var pt))
                {
                    var ptRes = TypeId.Parse(pt.GetString());
                    if (ptRes.IsSuccess) pType = ptRes.Value;
                }

                defaultLinks.Add(new DefaultLink(kind, pCat, pType));
            }
        }

        if (semanticErrors.Count > 0)
        {
            return Error.Validation("common.validation.failed", semanticErrors);
        }

        return new DeviceTypeDocument(
            idRes.Value,
            verRes.Value,
            role,
            category,
            extends,
            caps,
            attributes,
            childTypes,
            licenceClass,
            display,
            stateSummary,
            alarmRules,
            defaultLinks);
    }

    public Result<ManifestDocument> ReadManifest(ReadOnlySpan<byte> utf8, string sourcePath)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(utf8);
        }
        catch (JsonException ex)
        {
            return Error.Validation("common.validation.failed", [CommonErrors.InvalidFormatAt(string.Empty, ex.Message)]);
        }

        if (node is null)
        {
            return Error.Validation("common.validation.failed", [CommonErrors.RequiredAt(string.Empty)]);
        }

        using var doc = JsonDocument.Parse(utf8.ToArray());
        var root = doc.RootElement;

        var validationResult = _manifestSchema.Evaluate(root, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        if (!validationResult.IsValid)
        {
            return ConvertEvaluationErrors(validationResult);
        }

        var semanticErrors = new List<FieldError>();

        bool nativeCode = root.GetProperty("nativeCode").GetBoolean();
        string isoStr = root.GetProperty("isolation").GetString()!;
        if (nativeCode && isoStr != "isolatedRequired")
        {
            semanticErrors.Add(FieldError.Create("/isolation", ErrorCode.FromTrusted("scm.manifest.native_requires_isolation"),
                ("isolation", isoStr)));
        }

        var connectors = new List<ConnectorDefinitionDoc>();
        int connIdx = 0;
        foreach (var c in root.GetProperty("connectors").EnumerateArray())
        {
            string cls = c.GetProperty("class").GetString()!;
            if (!cls.Contains("::", StringComparison.Ordinal))
            {
                semanticErrors.Add(CommonErrors.InvalidFormatAt($"/connectors/{connIdx}/class", "<AssemblyName>::<FullTypeName>"));
            }

            string srvType = c.GetProperty("serverType").GetString()!;
            string suppVer = c.GetProperty("supportedVendorVersions").GetString()!;
            var features = c.GetProperty("features").EnumerateArray().Select(x => x.GetString()!).ToList();

            string? settingsPath = null;
            ParameterSchema? inlineSettings = null;
            if (c.TryGetProperty("settingsSchema", out var ssElem))
            {
                if (ssElem.ValueKind == JsonValueKind.String)
                {
                    settingsPath = ssElem.GetString();
                }
                else
                {
                    var pRes = ParameterSchema.Parse(ssElem);
                    if (pRes.IsSuccess) inlineSettings = pRes.Value;
                }
            }

            SecretsSchemaDoc? secrets = null;
            if (c.TryGetProperty("secretsSchema", out var secElem) && secElem.TryGetProperty("fields", out var fArray))
            {
                var sFields = new List<SecretFieldDoc>();
                foreach (var f in fArray.EnumerateArray())
                {
                    sFields.Add(new SecretFieldDoc(
                        f.GetProperty("id").GetString()!,
                        f.GetProperty("kind").GetString()!,
                        f.GetProperty("required").GetBoolean()));
                }
                secrets = new SecretsSchemaDoc(sFields);
            }

            ConnectorLimitsDoc? limits = null;
            if (c.TryGetProperty("limits", out var limElem))
            {
                limits = new ConnectorLimitsDoc(
                    limElem.TryGetProperty("devicesPerConnection", out var dpc) ? dpc.GetInt32() : null,
                    limElem.TryGetProperty("commandsPerSecond", out var cps) ? cps.GetInt32() : null);
            }

            connectors.Add(new ConnectorDefinitionDoc(
                cls,
                srvType,
                suppVer,
                features,
                settingsPath,
                inlineSettings,
                secrets,
                limits));

            connIdx++;
        }

        if (semanticErrors.Count > 0)
        {
            return Error.Validation("common.validation.failed", semanticErrors);
        }

        var id = PackageId.Parse(root.GetProperty("id").GetString()!).Value;
        var version = SemVersion.Parse(root.GetProperty("version").GetString()!).Value;
        string vendor = root.GetProperty("vendor").GetString()!;
        Enum.TryParse<Category>(root.GetProperty("category").GetString()!, ignoreCase: true, out var cat);
        var contract = SemVersionRange.Parse(root.GetProperty("contract").GetString()!).Value;
        var minWpVer = SemVersion.Parse(root.GetProperty("minWatchpointVersion").GetString()!).Value;
        var platforms = root.GetProperty("platforms").EnumerateArray().Select(x => x.GetString()!).ToList();

        Enum.TryParse<PackageIsolation>(isoStr, ignoreCase: true, out var isolation);

        var licElem = root.GetProperty("licence");
        Enum.TryParse<ManifestLicenceClass>(licElem.GetProperty("class").GetString()!, ignoreCase: true, out var licClass);
        var licence = new ManifestLicenceDoc(licClass, licElem.TryGetProperty("featureKey", out var fk) ? fk.GetString() : null);

        var types = root.GetProperty("types").EnumerateArray().Select(x => x.GetString()!).ToList();
        var capabilities = root.GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()!).ToList();
        var ruleTemplates = root.GetProperty("ruleTemplates").EnumerateArray().Select(x => x.GetString()!).ToList();

        var files = root.GetProperty("files").EnumerateArray().Select(f =>
            new PackageFileDoc(f.GetProperty("path").GetString()!, f.GetProperty("sha256").GetString()!)).ToList();

        return new ManifestDocument(
            id,
            version,
            vendor,
            cat,
            contract,
            minWpVer,
            platforms,
            nativeCode,
            isolation,
            licence,
            connectors,
            types,
            capabilities,
            ruleTemplates,
            files,
            root.TryGetProperty("symbols", out var sym) ? sym.GetString() : null,
            root.TryGetProperty("i18n", out var i18n) ? i18n.GetString() : null,
            root.TryGetProperty("docs", out var d) ? d.GetString() : null,
            root.TryGetProperty("privacy", out var priv) && priv.TryGetProperty("personalData", out var pd)
                ? new PackagePrivacyDoc(pd.EnumerateArray().Select(x => x.GetString()!).ToList())
                : null);
    }

    private static Result<TimeSpan> ParseIsoDuration(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return CommonErrors.InvalidFormat("ISO 8601 duration");
        }

        try
        {
            var ts = XmlConvert.ToTimeSpan(input);
            return ts;
        }
        catch
        {
            return CommonErrors.InvalidFormat("ISO 8601 duration");
        }
    }

    private static Error ConvertEvaluationErrors(EvaluationResults results)
    {
        var fieldErrors = new List<FieldError>();
        CollectErrors(results, fieldErrors);
        return Error.Validation("common.validation.failed", fieldErrors);
    }

    private static void CollectErrors(EvaluationResults results, List<FieldError> fieldErrors)
    {
        if (!results.IsValid && results.Errors != null)
        {
            foreach (var err in results.Errors)
            {
                string path = results.InstanceLocation.ToString();
                fieldErrors.Add(CommonErrors.InvalidFormatAt(path, err.Value));
            }
        }

        if (results.Details != null)
        {
            foreach (var detail in results.Details)
            {
                CollectErrors(detail, fieldErrors);
            }
        }
    }
}
