using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Abstractions.Model;

/// <summary>Priority of outbound work of a connection (commands, polling, refresh).</summary>
public enum CommandPriority
{
    /// <summary>Life safety and security commands.</summary>
    Critical,

    /// <summary>Operator commands.</summary>
    High,

    /// <summary>Normal work.</summary>
    Normal,

    /// <summary>Polling, refresh, housekeeping.</summary>
    Background,
}

/// <summary>A command dispatched to a connector (INT §6).</summary>
/// <param name="CommandId">The command id (stable across attempts).</param>
/// <param name="Target">The target device.</param>
/// <param name="Command">The command path, e.g. <c>openable.open</c>.</param>
/// <param name="Arguments">The arguments, validated against the command definition.</param>
/// <param name="Deadline">When the command must be completed.</param>
/// <param name="Attempt">The attempt number, starting at 1.</param>
/// <param name="Priority">The priority.</param>
public sealed record ConnectorCommand(Guid CommandId, DeviceHandle Target, string Command,
    IReadOnlyDictionary<string, WpValue> Arguments, DateTimeOffset Deadline, int Attempt, CommandPriority Priority);

/// <summary>Outcome of a command.</summary>
public enum CommandOutcome
{
    /// <summary>The system accepted and executed the command.</summary>
    Succeeded,

    /// <summary>The command failed.</summary>
    Failed,

    /// <summary>The device does not support the command.</summary>
    NotSupported,
}

/// <summary>Result of a command (INT §6). A failed result always has a category and a message; other outcomes have neither.</summary>
public sealed record ConnectorCommandResult
{
    private static readonly ConnectorCommandResult _succeeded = new(CommandOutcome.Succeeded, null, null, null, null);
    private static readonly ConnectorCommandResult _notSupported = new(CommandOutcome.NotSupported, null, null, null, null);

    /// <summary>Initialises a result.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <param name="failure">The failure category (required when failed, otherwise null).</param>
    /// <param name="message">The message (required when failed).</param>
    /// <param name="vendorCode">The vendor's error code, if any.</param>
    /// <param name="vendorDetail">Vendor detail: never shown to operators, logged at debug.</param>
    /// <exception cref="ArgumentException">The combination is inconsistent.</exception>
    public ConnectorCommandResult(CommandOutcome outcome, FailureCategory? failure, ConnectorMessage? message,
        string? vendorCode, string? vendorDetail)
    {
        if (outcome == CommandOutcome.Failed && (failure is null || message is null))
        {
            throw new ArgumentException("A failed command result needs a failure category and a message.", nameof(failure));
        }

        if (outcome != CommandOutcome.Failed && failure is not null)
        {
            throw new ArgumentException("Only a failed command result has a failure category.", nameof(failure));
        }

        Outcome = outcome;
        Failure = failure;
        Message = message;
        VendorCode = vendorCode;
        VendorDetail = vendorDetail;
    }

    /// <summary>Gets the outcome.</summary>
    public CommandOutcome Outcome { get; }

    /// <summary>Gets the failure category (failed results only).</summary>
    public FailureCategory? Failure { get; }

    /// <summary>Gets the message.</summary>
    public ConnectorMessage? Message { get; }

    /// <summary>Gets the vendor's error code.</summary>
    public string? VendorCode { get; }

    /// <summary>Gets vendor detail (never shown to operators; logged at debug).</summary>
    public string? VendorDetail { get; }

    /// <summary>A successful result.</summary>
    /// <returns>The result.</returns>
    public static ConnectorCommandResult Succeeded() => _succeeded;

    /// <summary>A failed result.</summary>
    /// <param name="failure">The failure category.</param>
    /// <param name="message">The message.</param>
    /// <param name="vendorCode">The vendor's error code, if any.</param>
    /// <returns>The result.</returns>
    public static ConnectorCommandResult Failed(FailureCategory failure, ConnectorMessage message, string? vendorCode = null) =>
        new(CommandOutcome.Failed, failure, message ?? throw new ArgumentNullException(nameof(message)), vendorCode, null);

    /// <summary>The device does not support the command.</summary>
    /// <returns>The result.</returns>
    public static ConnectorCommandResult NotSupported() => _notSupported;
}

/// <summary>What <c>IStateRefresh.RefreshAsync</c> reads (INT §6).</summary>
/// <param name="Devices">The devices to refresh; <see langword="null"/> = all.</param>
/// <param name="IncludeServers">Whether server states are refreshed too.</param>
public sealed record RefreshScope(IReadOnlyList<DeviceHandle>? Devices, bool IncludeServers)
{
    /// <summary>Everything: all devices and servers.</summary>
    public static RefreshScope All { get; } = new(null, true);
}
