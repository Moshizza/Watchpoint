namespace Watchpoint.Integration.Mqtt;

public sealed record MqttDiagnosticMethod(string Name, string Description);

public static class MqttDiagnostics
{
    public static readonly IReadOnlyList<MqttDiagnosticMethod> Methods =
    [
        new("sniff", "Capture live raw MQTT packets on topics for diagnostics"),
        new("testMapping", "Dry-run a JSON payload against configured mapping rules"),
        new("publish", "Publish an arbitrary diagnostic MQTT packet"),
        new("stats", "Return connection statistics and counters")
    ];
}
