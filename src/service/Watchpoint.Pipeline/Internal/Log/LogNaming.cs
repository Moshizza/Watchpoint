using System.Globalization;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal.Log;

/// <summary>Stream names and subjects of the event log (spec PIP §5.1), with the configured prefixes.</summary>
internal sealed class LogNaming(string subjectPrefix, string streamPrefix)
{
    public LogNaming(PipelineOptions options)
        : this(options.SubjectPrefix, options.StreamPrefix)
    {
    }

    /// <summary>The stream name, e.g. <c>WP_LOG</c>.</summary>
    public string StreamName(LogStream stream) => streamPrefix + stream switch
    {
        LogStream.Log => "WP_LOG",
        LogStream.Cfg => "WP_CFG",
        LogStream.Sys => "WP_SYS",
        _ => throw new ArgumentOutOfRangeException(nameof(stream), stream, "Unknown stream."),
    };

    /// <summary>The subject filter that captures everything of a stream, e.g. <c>wp.log.&gt;</c>.</summary>
    public string StreamSubjects(LogStream stream) => Root(stream) + ".>";

    /// <summary>The subject of a record: <c>wp.log.&lt;pp&gt;.&lt;kind&gt;</c>, or <c>wp.cfg.&lt;kind&gt;</c> / <c>wp.sys.&lt;kind&gt;</c>.</summary>
    public string Subject(LogStream stream, int partition, string kind) => stream == LogStream.Log
        ? string.Create(CultureInfo.InvariantCulture, $"{Root(stream)}.{CheckPartition(partition):00}.{kind}")
        : Root(stream) + "." + kind;

    /// <summary>The filter of one partition: <c>wp.log.&lt;pp&gt;.&gt;</c>.</summary>
    public string PartitionFilter(int partition) =>
        string.Create(CultureInfo.InvariantCulture, $"{Root(LogStream.Log)}.{CheckPartition(partition):00}.>");

    /// <summary>One filter per partition, or the whole log when all partitions are asked.</summary>
    public IReadOnlyList<string> PartitionFilters(IReadOnlySet<int> partitions)
    {
        if (partitions.Count == Partitioning.PartitionCount && partitions.All(Partitioning.IsValid))
        {
            return [StreamSubjects(LogStream.Log)];
        }

        return [.. partitions.Order().Select(PartitionFilter)];
    }

    /// <summary>The subject filters of a consumer for kinds or kind patterns (empty: everything).</summary>
    public IReadOnlyList<string> KindFilters(LogStream stream, IReadOnlyList<string> kindFilter)
    {
        if (kindFilter.Count == 0)
        {
            return [StreamSubjects(stream)];
        }

        var prefix = stream == LogStream.Log ? Root(stream) + ".*." : Root(stream) + ".";
        return [.. kindFilter.Distinct(StringComparer.Ordinal).Select(pattern => prefix + pattern)];
    }

    /// <summary>
    /// A kind pattern is a kind where a token may be <c>*</c> and the last token may be <c>&gt;</c>, e.g. <c>alarm.*</c>.
    /// </summary>
    public static bool IsValidKindPattern(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern.Length > LogEnvelope.MaxKindLength)
        {
            return false;
        }

        var tokens = pattern.Split('.');
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var wildcard = token == "*" || (token == ">" && i == tokens.Length - 1);
            if (!wildcard && !LogEnvelope.IsValidKind(token))
            {
                return false;
            }
        }

        return tokens.Length <= 4;
    }

    /// <summary>Returns whether a subject matches a NATS subject filter (<c>*</c> one token, trailing <c>&gt;</c> the rest).</summary>
    public static bool Matches(string filter, string subject)
    {
        var f = filter.Split('.');
        var s = subject.Split('.');
        for (var i = 0; i < f.Length; i++)
        {
            if (f[i] == ">")
            {
                return s.Length > i;
            }

            if (i >= s.Length || (f[i] != "*" && !string.Equals(f[i], s[i], StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return f.Length == s.Length;
    }

    private static int CheckPartition(int partition) => Partitioning.IsValid(partition)
        ? partition
        : throw new ArgumentOutOfRangeException(nameof(partition), partition, "Partition must be 0 to 63.");

    private string Root(LogStream stream) => subjectPrefix + stream switch
    {
        LogStream.Log => ".log",
        LogStream.Cfg => ".cfg",
        LogStream.Sys => ".sys",
        _ => throw new ArgumentOutOfRangeException(nameof(stream), stream, "Unknown stream."),
    };
}
