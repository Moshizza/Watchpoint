using Microsoft.Extensions.Logging;

namespace Watchpoint.Hosting;

/// <summary>Decides whether the development seed may run (spec HST §9, §12): only when <c>Node:Environment = Development</c>.</summary>
public static class DevSeedGate
{
    /// <summary>Returns whether the seed in <c>Dev:SeedFile</c> must be applied; outside Development logs <c>HST1140</c> and skips it.</summary>
    /// <param name="settings">The node settings.</param>
    /// <param name="logger">The logger.</param>
    /// <returns><see langword="true"/> when a seed file is set and the node runs in Development.</returns>
    public static bool ShouldRun(NodeSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrWhiteSpace(settings.Dev.SeedFile))
        {
            return false;
        }

        if (settings.Node.Environment != NodeEnvironment.Development)
        {
            HostingLog.LogDevSeedFailed(logger, "environment", $"seed refused outside Development (environment {settings.Node.Environment})");
            return false;
        }

        return true;
    }
}
