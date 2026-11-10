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
}
