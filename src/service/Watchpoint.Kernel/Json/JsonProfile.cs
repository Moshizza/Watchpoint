namespace Watchpoint.Kernel.Json;

/// <summary>Serialisation profile of <see cref="WatchpointJson.Create"/>; decides how unknown members are handled.</summary>
public enum JsonProfile
{
    /// <summary>API bodies: unknown members ignored on input (forward compatibility of clients).</summary>
    Api,

    /// <summary>Configuration documents: unknown members rejected with a <see cref="System.Text.Json.JsonException"/>.</summary>
    Document,

    /// <summary>Event log records: unknown members ignored (records written by newer versions).</summary>
    Log,
}
