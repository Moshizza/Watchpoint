namespace Watchpoint.Pipeline.Contracts;

/// <summary>The record kinds of M1 (spec PIP §5.3). Other modules register their own kinds.</summary>
public static class RecordKinds
{
    /// <summary>A property value change (<see cref="PropertyRecordBody"/>), stream <see cref="LogStream.Log"/>.</summary>
    public const string Property = "property";

    /// <summary>A device event (<see cref="EventRecordBody"/>), stream <see cref="LogStream.Log"/>.</summary>
    public const string Event = "event";

    /// <summary>A full health state (<see cref="HealthRecordBody"/>), stream <see cref="LogStream.Log"/>.</summary>
    public const string Health = "health";

    /// <summary>An applied registry batch (<see cref="RegistryChangedBody"/>), stream <see cref="LogStream.Cfg"/>.</summary>
    public const string RegistryChanged = "registry.changed";
}
