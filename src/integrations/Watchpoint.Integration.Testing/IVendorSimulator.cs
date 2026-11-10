namespace Watchpoint.Integration.Testing;

/// <summary>
/// Hook interface for vendor system simulators used by connector conformance testing.
/// </summary>
public interface IVendorSimulator : IAsyncDisposable
{
    /// <summary>Starts or initializes the simulator.</summary>
    ValueTask StartAsync(CancellationToken ct = default);

    /// <summary>Stops the simulator.</summary>
    ValueTask StopAsync(CancellationToken ct = default);

    /// <summary>Simulates a dropped connection (e.g. socket close, network partition).</summary>
    ValueTask DropConnectionAsync(CancellationToken ct = default);

    /// <summary>Restores connection after a drop.</summary>
    ValueTask RestoreConnectionAsync(CancellationToken ct = default);

    /// <summary>Injects a state/property change into the simulator.</summary>
    ValueTask InjectStateChangeAsync(string nativeId, string propertyPath, object? value, CancellationToken ct = default);

    /// <summary>Injects an event into the simulator.</summary>
    ValueTask InjectEventAsync(string nativeId, string eventPath, IReadOnlyDictionary<string, object?>? payload = null, CancellationToken ct = default);

    /// <summary>Configures the simulator to fail or refuse the next command.</summary>
    void FailNextCommand(string? failureCategory = null, string? messageKey = null, IReadOnlyDictionary<string, object?>? messageArgs = null);

    /// <summary>Configures the simulator to delay the next command beyond the deadline.</summary>
    void DelayNextCommand(TimeSpan delay);

    /// <summary>Renames a device in the simulator to verify native ID stability vs display name.</summary>
    ValueTask RenameDeviceAsync(string nativeId, string newDisplayName, CancellationToken ct = default);

    /// <summary>Gets the number of requests received by the simulator.</summary>
    long GetRequestCount();

    /// <summary>Resets the request counter.</summary>
    void ResetRequestCount();
}
