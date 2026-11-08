using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Results;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Internal;

/// <summary>Builds the <see cref="StoreMap"/> once from settings and the installed providers.</summary>
internal sealed class StoreMapHolder(StorageSettings settings, IEnumerable<IStorageProvider> providers, StorageEnvironment environment)
{
    private readonly Lazy<Result<StoreMap>> _map = new(() => StoreMap.Build(settings, providers, environment.IsDevelopment));

    public Result<StoreMap> Result => _map.Value;

    /// <summary>The map; invalid settings throw <see cref="StorageStartupException"/> with exit code 78.</summary>
    public StoreMap Map => _map.Value.TryGetValue(out var map, out var error)
        ? map
        : throw new StorageStartupException(error, StorageStartupException.Config);

    public void LogWarnings(ILogger logger)
    {
        foreach (var warning in Map.Warnings)
        {
            StorageLog.SettingsWarning(logger, warning);
        }
    }
}

/// <summary>Whether the node runs in Development (clear-text dev passwords allowed).</summary>
internal sealed record StorageEnvironment(bool IsDevelopment);
