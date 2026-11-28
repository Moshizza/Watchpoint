using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Watchpoint.Audit.Internal;

/// <summary>The fallback files <c>fallback-&lt;date&gt;.jsonl</c> that receive records that could not be queued or written (spec AUD §5): one line per record, flushed to disk each time.</summary>
internal sealed class FallbackFile(string directory, TimeProvider clock, ILogger logger)
{
    private readonly Lock _gate = new();

    public void Append(PreparedRecord record)
    {
        var line = JsonSerializer.Serialize(record, AuditJsonContext.Default.PreparedRecord) + "\n";
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "fallback-" + clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            var bytes = Encoding.UTF8.GetBytes(line);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>Reads the fallback files in order.</summary>
    public IEnumerable<(string Path, IReadOnlyList<PreparedRecord> Records)> ReadAll()
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "fallback-*.jsonl").Order(StringComparer.Ordinal))
        {
            List<PreparedRecord> records = [];
            var number = 0;
            foreach (var line in File.ReadLines(path))
            {
                number++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var record = JsonSerializer.Deserialize(line, AuditJsonContext.Default.PreparedRecord);
                    if (record is not null)
                    {
                        records.Add(record);
                    }
                }
                catch (JsonException)
                {
                    AuditLog.FallbackLineSkipped(logger, Path.GetFileName(path), number);
                }
            }

            yield return (path, records);
        }
    }

    public static void Delete(string path)
    {
        File.Delete(path);
    }
}
