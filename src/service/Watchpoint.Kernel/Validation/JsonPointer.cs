using System.Text;

namespace Watchpoint.Kernel.Validation;

/// <summary>Builds JSON Pointers (RFC 6901) for field errors: segments are escaped (<c>~</c> → <c>~0</c>, <c>/</c> → <c>~1</c>).</summary>
public static class JsonPointer
{
    /// <summary>Escapes one segment.</summary>
    /// <param name="segment">The raw segment, e.g. a property name or an index.</param>
    /// <returns>The escaped segment.</returns>
    public static string Escape(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    /// <summary>Builds a pointer from raw segments: <c>("a/b~c", "0")</c> → <c>/a~1b~0c/0</c>.</summary>
    /// <param name="segments">The raw segments.</param>
    /// <returns>The pointer; empty for no segment (the whole document).</returns>
    public static string Combine(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            sb.Append('/').Append(Escape(segment));
        }

        return sb.ToString();
    }

    /// <summary>Appends one raw segment to an existing pointer.</summary>
    /// <param name="path">The pointer (already escaped).</param>
    /// <param name="segment">The raw segment.</param>
    /// <returns>The pointer.</returns>
    public static string Append(string path, string segment)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path + "/" + Escape(segment);
    }

    /// <summary>Appends an array index to an existing pointer.</summary>
    /// <param name="path">The pointer (already escaped).</param>
    /// <param name="index">The index.</param>
    /// <returns>The pointer.</returns>
    public static string Append(string path, int index)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return path + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
