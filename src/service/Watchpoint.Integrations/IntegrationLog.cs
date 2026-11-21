using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Documents;

namespace Watchpoint.Integrations;

/// <summary>Log events of the integration runtime (event ids 1800–1999, spec IRT §13).</summary>
internal static partial class IntegrationLog
{
    [LoggerMessage(EventId = 1801, Level = LogLevel.Information, Message = "Package {Package}@{Version} found ({Source})")]
    public static partial void PackageFound(ILogger logger, PackageId package, SemVersion version, string source);

    [LoggerMessage(EventId = 1802, Level = LogLevel.Warning, Message = "Package {Package}@{Version} invalid: {Problems}")]
    public static partial void PackageInvalid(ILogger logger, PackageId package, SemVersion version, string problems);

    [LoggerMessage(EventId = 1803, Level = LogLevel.Information, Message = "Package {Package}@{Version} active")]
    public static partial void PackageActive(ILogger logger, PackageId package, SemVersion version);

    [LoggerMessage(EventId = 1804, Level = LogLevel.Warning, Message = "Package {Package}@{Version} signed with a development key")]
    public static partial void PackageDevelopmentKey(ILogger logger, PackageId package, SemVersion version);

    [LoggerMessage(EventId = 1805, Level = LogLevel.Information, Message = "Package {Package}@{Version} {State} by {Actor}")]
    public static partial void PackageStateSet(ILogger logger, PackageId package, SemVersion version, Contracts.PackageState state, UserId actor);

    [LoggerMessage(EventId = 1806, Level = LogLevel.Information, Message = "Package {Package}@{Version} removed")]
    public static partial void PackageRemoved(ILogger logger, PackageId package, SemVersion version);

    [LoggerMessage(EventId = 1807, Level = LogLevel.Warning, Message = "Package {Package}@{Version}: file {Path} is missing but connections still use it")]
    public static partial void PackageFileMissing(ILogger logger, PackageId package, SemVersion version, string path);

    [LoggerMessage(EventId = 1808, Level = LogLevel.Warning, Message = "Package folder watcher lost events for {Folder}; rescanning")]
    public static partial void WatcherOverflow(ILogger logger, Exception? exception, string folder);

    [LoggerMessage(EventId = 1809, Level = LogLevel.Debug, Message = "Package source {Path} is still being written; retrying")]
    public static partial void PackageSourceBusy(ILogger logger, string path);

    [LoggerMessage(EventId = 1810, Level = LogLevel.Error, Message = "Package scan failed")]
    public static partial void ScanFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1811, Level = LogLevel.Warning, Message = "Package state of {Package}@{Version} could not be saved")]
    public static partial void PackageStoreFailed(ILogger logger, Exception exception, PackageId package, SemVersion version);

    [LoggerMessage(EventId = 1812, Level = LogLevel.Error, Message = "A handler of the package catalogue Changed event failed")]
    public static partial void ChangedHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1813, Level = LogLevel.Warning, Message = "Development key {File} ignored: {Reason}")]
    public static partial void DevelopmentKeyIgnored(ILogger logger, string file, string reason);

    [LoggerMessage(EventId = 1814, Level = LogLevel.Warning, Message = "Package {Package}@{Version} ships its own copy of shared assembly {Assembly}; the host's copy is used")]
    public static partial void SharedAssemblyShadowed(ILogger logger, PackageId package, SemVersion version, string assembly);

    [LoggerMessage(EventId = 1815, Level = LogLevel.Information, Message = "Package {FileName} installed by {Actor}")]
    public static partial void PackageInstalled(ILogger logger, string fileName, UserId actor);

    [LoggerMessage(EventId = 1816, Level = LogLevel.Debug, Message = "Load context {Context} unloaded")]
    public static partial void LoadContextUnloaded(ILogger logger, string context);

    [LoggerMessage(EventId = 1820, Level = LogLevel.Information, Message = "Connection {ConnectionId} ({Name}) state {From} → {To}: {Reason}")]
    public static partial void ConnectionStateChanged(ILogger logger, Guid connectionId, string name, Contracts.ConnectionState from, Contracts.ConnectionState to, string reason);

    [LoggerMessage(EventId = 1821, Level = LogLevel.Error, Message = "A handler of the connection StatusChanged event failed")]
    public static partial void StatusHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1822, Level = LogLevel.Warning, Message = "State {State} of connection {ConnectionId} could not be recorded")]
    public static partial void StateRecordFailed(ILogger logger, Exception exception, Guid connectionId, Contracts.ConnectionState state);

    [LoggerMessage(EventId = 1823, Level = LogLevel.Error, Message = "Runtime of connection {ConnectionId} failed to process {Operation}")]
    public static partial void RuntimeOperationFailed(ILogger logger, Exception exception, Guid connectionId, string operation);

    [LoggerMessage(EventId = 1824, Level = LogLevel.Information, Message = "Connection {ConnectionId} ({Name}) {Action} by {Actor}")]
    public static partial void ConnectionChanged(ILogger logger, Guid connectionId, string name, string action, string actor);

    [LoggerMessage(EventId = 1825, Level = LogLevel.Warning, Message = "Connection {ConnectionId} could not be loaded: {Reason}")]
    public static partial void ConnectionLoadFailed(ILogger logger, Guid connectionId, string reason);

    [LoggerMessage(EventId = 1826, Level = LogLevel.Warning, Message = "Connection {ConnectionId}: plain HTTP to vendors is allowed by its settings")]
    public static partial void InsecureHttpAllowed(ILogger logger, Guid connectionId);

    [LoggerMessage(EventId = 1830, Level = LogLevel.Information, Message = "Connection {ConnectionId} reconciled: {Summary}")]
    public static partial void Reconciled(ILogger logger, Guid connectionId, string summary);

    [LoggerMessage(EventId = 1831, Level = LogLevel.Warning, Message = "Connection {ConnectionId}: reconciliation failed: {Code}")]
    public static partial void ReconcileFailed(ILogger logger, Guid connectionId, string code);

    [LoggerMessage(EventId = 1832, Level = LogLevel.Warning, Message = "Connection {ConnectionId}: inventory item {NativeId} skipped, type {TypeId}@{TypeVersion} unknown")]
    public static partial void InventoryTypeUnknown(ILogger logger, Guid connectionId, string nativeId, string typeId, string typeVersion);

    [LoggerMessage(EventId = 1833, Level = LogLevel.Warning, Message = "Connection {ConnectionId}: the device map could not be rebuilt")]
    public static partial void DeviceMapFailed(ILogger logger, Exception exception, Guid connectionId);

    [LoggerMessage(EventId = 1840, Level = LogLevel.Warning, Message = "Connection {ConnectionId} dropped {Count} reports for unknown devices in the last minute")]
    public static partial void UnknownDevices(ILogger logger, Guid connectionId, long count);

    [LoggerMessage(EventId = 1841, Level = LogLevel.Warning, Message = "Connection {ConnectionId}: the ingest failed for a batch of {Count} observations; retrying")]
    public static partial void SinkIngestFailed(ILogger logger, Exception exception, Guid connectionId, int count);

    [LoggerMessage(EventId = 1842, Level = LogLevel.Warning, Message = "Connection {ConnectionId}: observation rejected: {Code} ({Target})")]
    public static partial void ObservationRejected(ILogger logger, Guid connectionId, string code, string target);

    [LoggerMessage(EventId = 1843, Level = LogLevel.Warning, Message = "Connection {ConnectionId}: {Count} buffered observations dropped at stop")]
    public static partial void SinkDropped(ILogger logger, Guid connectionId, int count);

    [LoggerMessage(EventId = 1844, Level = LogLevel.Warning, Message = "Connection {ConnectionId}: the store of the connection could not be read or written")]
    public static partial void StoreFailed(ILogger logger, Exception exception, Guid connectionId);

    [LoggerMessage(EventId = 1845, Level = LogLevel.Warning, Message = "Connection {ConnectionId}: secret field {Field} could not be read: {Code}")]
    public static partial void SecretUnavailable(ILogger logger, Guid connectionId, string field, string code);

    [LoggerMessage(EventId = 1846, Level = LogLevel.Warning, Message = "Connection {ConnectionId}: inbound request to {Path} refused ({Reason})")]
    public static partial void InboundRefused(ILogger logger, Guid connectionId, string path, string reason);

    [LoggerMessage(EventId = 1847, Level = LogLevel.Warning, Message = "Connection {ConnectionId}: the inbound handler for {Path} failed")]
    public static partial void InboundHandlerFailed(ILogger logger, Exception exception, Guid connectionId, string path);

    [LoggerMessage(EventId = 1850, Level = LogLevel.Error, Message = "Connector {ConnectionId} faulted: {Exception}")]
    public static partial void ConnectorFaulted(ILogger logger, Guid connectionId, string exception);

    [LoggerMessage(EventId = 1851, Level = LogLevel.Error, Message = "Connector {ConnectionId} did not stop within timeout; instance abandoned")]
    public static partial void ConnectorAbandoned(ILogger logger, Guid connectionId);

    [LoggerMessage(EventId = 1852, Level = LogLevel.Warning, Message = "Connector {ConnectionId}: initialisation refused: {Code}")]
    public static partial void ConnectorInitializeRefused(ILogger logger, Guid connectionId, string code);

    [LoggerMessage(EventId = 1853, Level = LogLevel.Warning, Message = "Connection {ConnectionId} restarts in {Delay} (attempt {Attempt})")]
    public static partial void RestartScheduled(ILogger logger, Guid connectionId, TimeSpan delay, int attempt);

    [LoggerMessage(EventId = 1860, Level = LogLevel.Error, Message = "Watchdog: command {CommandId} on connection {ConnectionId} hung past its deadline; restarting the connection")]
    public static partial void HungCommand(ILogger logger, Guid connectionId, Guid commandId);

    [LoggerMessage(EventId = 1861, Level = LogLevel.Error, Message = "Watchdog: StartAsync of connection {ConnectionId} did not return within {Timeout}")]
    public static partial void HungStart(ILogger logger, Guid connectionId, TimeSpan timeout);

    [LoggerMessage(EventId = 1862, Level = LogLevel.Warning, Message = "Watchdog: connection {ConnectionId} has been silent for {Silence}")]
    public static partial void SilentConnector(ILogger logger, Guid connectionId, TimeSpan silence);

    [LoggerMessage(EventId = 1863, Level = LogLevel.Warning, Message = "Connection {ConnectionId} flooding {Flooding} ({Pending} pending)")]
    public static partial void Flooding(ILogger logger, Guid connectionId, bool flooding, int pending);

    [LoggerMessage(EventId = 1864, Level = LogLevel.Error, Message = "Watchdog check failed")]
    public static partial void WatchdogFailed(ILogger logger, Exception exception);
}
