using System.Text.Json;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Model.Documents;

public sealed record CapabilityDocument(
    CapabilityId Id,
    SemVersion Version,
    IReadOnlyList<PropertyDefinitionDoc> Properties,
    IReadOnlyList<CommandDefinitionDoc> Commands,
    IReadOnlyList<EventDefinitionDoc> Events);

public sealed record PropertyDefinitionDoc(
    MemberId Id,
    string ValueType,
    IReadOnlyList<EnumValueDoc>? EnumValues = null,
    string? Unit = null,
    RangeDoc? Range = null,
    int? Decimals = null,
    int? MaxLength = null,
    string? ItemType = null,
    ParameterSchema? ObjectSchema = null,
    bool Writable = false,
    HistoryConfigDoc? History = null,
    TimeSpan? StaleAfter = null,
    JsonElement? Default = null,
    BooleanSeverityDoc? BooleanSeverity = null);

public sealed record EnumValueDoc(MemberId Code, string Severity);

public sealed record RangeDoc(double? Min, double? Max);

public sealed record HistoryConfigDoc(string Mode, string? Type = null, double? Threshold = null, TimeSpan? Interval = null);

public sealed record BooleanSeverityDoc(string True, string False);

public sealed record CommandDefinitionDoc(
    MemberId Id,
    ParameterSchema Parameters,
    string Risk = "normal",
    TimeSpan? Timeout = null,
    bool Idempotent = false,
    bool Batchable = false,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? RiskByArgument = null,
    ExpectedEffectDoc? ExpectedEffect = null);

public sealed record ExpectedEffectDoc(
    string Property,
    JsonElement? EqualsValue = null,
    IReadOnlyList<JsonElement>? In = null,
    TimeSpan? Within = null);

public sealed record EventDefinitionDoc(
    MemberId Id,
    string Severity = "info",
    ParameterSchema? Payload = null,
    IReadOnlyList<string>? PersonalData = null);
