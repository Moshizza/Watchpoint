using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Model;

/// <summary>Log events for the Model module (event ids 1400–1499, spec MOD §10).</summary>
internal static partial class ModelLog
{
    [LoggerMessage(EventId = 1401, Level = LogLevel.Information, Message = "Standard library loaded: {Capabilities} capabilities, {Types} types in {DurationMs} ms")]
    public static partial void StandardLibraryLoaded(ILogger logger, int capabilities, int types, double durationMs);

    [LoggerMessage(EventId = 1402, Level = LogLevel.Information, Message = "Package {Package}@{Version} registered: {Types} types, {Capabilities} capabilities")]
    public static partial void PackageRegistered(ILogger logger, PackageId package, string version, int types, int capabilities);

    [LoggerMessage(EventId = 1403, Level = LogLevel.Information, Message = "Package {Package}@{Version} unregistered")]
    public static partial void PackageUnregistered(ILogger logger, PackageId package, string version);

    [LoggerMessage(EventId = 1404, Level = LogLevel.Warning, Message = "Document warning {Code} at {Source}{Path}")]
    public static partial void DocumentWarning(ILogger logger, string code, string source, string path);

    [LoggerMessage(EventId = 1405, Level = LogLevel.Critical, Message = "Standard library invalid: {Problems}")]
    public static partial void StandardLibraryInvalid(ILogger logger, string problems);
}
