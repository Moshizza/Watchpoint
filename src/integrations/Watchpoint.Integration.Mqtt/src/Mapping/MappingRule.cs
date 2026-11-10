namespace Watchpoint.Integration.Mqtt.Mapping;

public sealed class MappingRule
{
    public required string Id { get; init; }
    public required TopicPattern Pattern { get; init; }
    public int Qos { get; init; } = 1;
    public string? PropertyPath { get; init; }
    public string? EventPath { get; init; }
    public string? DeviceNativeIdTemplate { get; init; }
    public string? DeviceTypeTemplate { get; init; }
    public string? DeviceNameTemplate { get; init; }
    public string? DeviceLocationHintTemplate { get; init; }
    public required PayloadFormat PayloadFormat { get; init; }
    public string? ValuePath { get; init; }
    public string? TimestampPath { get; init; }
    public string TimestampFormat { get; init; } = "iso8601";
    public double? Scale { get; init; }
    public double? Offset { get; init; }
    public Dictionary<string, string>? MapEnum { get; init; }
    public string TrueValue { get; init; } = "true";
    public string FalseValue { get; init; } = "false";
    public string? Unit { get; init; }
    public CommandRuleMapping? Command { get; init; }
    public TimeSpan? StaleAfter { get; init; }
}

public sealed class CommandRuleMapping
{
    public required string CommandName { get; init; }
    public required string PublishTopicTemplate { get; init; }
    public int Qos { get; init; } = 1;
    public bool Retain { get; init; }
    public string? PayloadTemplate { get; init; }
    public string? ResponseTopicTemplate { get; init; }
    public string? CorrelationDataTemplate { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
}

public enum PayloadFormat
{
    Json,
    Text,
    Number,
    Boolean,
    Raw
}
