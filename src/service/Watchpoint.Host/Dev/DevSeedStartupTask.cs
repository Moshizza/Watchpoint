using Watchpoint.Hosting;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Host.Dev;

/// <summary>
/// Loads the development seed (spec HST §9): phase <c>Configuration</c>, order 1000, only in Development and only
/// when no step finds existing data. The document is validated before anything is applied. A failure is logged
/// (<c>HST1140</c>) and never stops the node.
/// </summary>
public sealed partial class DevSeedStartupTask(NodeSettings settings, IEnumerable<IDevSeedStep> steps, ILogger<DevSeedStartupTask> logger)
    : IStartupTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Configuration;

    /// <inheritdoc />
    public int Order => 1000;

    /// <inheritdoc />
    public string Name => "DevSeed";

    /// <inheritdoc />
    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (!DevSeedGate.ShouldRun(settings, logger))
        {
            return;
        }

        var step = "read";
        try
        {
            var path = Path.GetFullPath(settings.Dev.SeedFile!);
            if (!File.Exists(path))
            {
                HostingLog.LogDevSeedFailed(logger, step, "seed file not found: " + path);
                return;
            }

            step = "validate";
            var read = DevSeedReader.Read(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
            if (!read.IsSuccess)
            {
                HostingLog.LogDevSeedFailed(logger, step, Describe(read.Error));
                return;
            }

            var seed = read.Value;
            var bySection = steps.ToDictionary(s => s.Section, StringComparer.Ordinal);
            foreach (var section in DevSeedDocument.Sections)
            {
                step = section;
                if (bySection.TryGetValue(section, out var existing) && await existing.HasDataAsync(seed, ct).ConfigureAwait(false))
                {
                    LogDevSeedNotNeeded(logger, section);
                    return;
                }
            }

            var applied = new List<string>();
            foreach (var section in DevSeedDocument.Sections.Where(seed.Has))
            {
                step = section;
                if (!bySection.TryGetValue(section, out var handler))
                {
                    // The module of this section is not composed into the host yet.
                    LogDevSeedSectionSkipped(logger, section);
                    continue;
                }

                var result = await handler.ApplyAsync(seed, ct).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    HostingLog.LogDevSeedFailed(logger, step, Describe(result.Error));
                    return;
                }

                applied.Add(section);
            }

            LogDevSeedApplied(logger, path, applied);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            HostingLog.LogDevSeedFailed(logger, step, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>The error code and, for a validation error, the paths in the document. Never values: a seed holds passwords.</summary>
    private static string Describe(Error error) => error.FieldErrors is { Count: > 0 } fields
        ? error.Code.Value + " at " + string.Join(", ", fields.Select(f => $"{(f.Path.Length == 0 ? "/" : f.Path)} ({f.Code.Value})"))
        : error.Code.Value;

    [LoggerMessage(EventId = 1141, Level = LogLevel.Warning, Message = "Development seed section {Section} skipped: no module of this node handles it")]
    private static partial void LogDevSeedSectionSkipped(ILogger logger, string section);

    [LoggerMessage(EventId = 1142, Level = LogLevel.Information, Message = "Development seed not applied: {Section} already has data")]
    private static partial void LogDevSeedNotNeeded(ILogger logger, string section);

    [LoggerMessage(EventId = 1143, Level = LogLevel.Information, Message = "Development seed {Path} applied: {Sections}")]
    private static partial void LogDevSeedApplied(ILogger logger, string path, List<string> sections);
}
