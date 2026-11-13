namespace Watchpoint.Integration.Mqtt.Mapping;

public sealed class TopicPattern
{
    private readonly string[] _segments;
    private readonly bool _hasMultiWildcard;

    public string RawPattern { get; }
    public string SubscriptionFilter { get; }
    public IReadOnlyList<string> Segments => _segments;
    public IReadOnlyList<string> CaptureNames { get; }

    private TopicPattern(string rawPattern, string[] segments, string subscriptionFilter, List<string> captureNames, bool hasMultiWildcard)
    {
        RawPattern = rawPattern;
        _segments = segments;
        SubscriptionFilter = subscriptionFilter;
        CaptureNames = captureNames;
        _hasMultiWildcard = hasMultiWildcard;
    }

    public static bool TryParse(string pattern, out TopicPattern? result, out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "Topic pattern cannot be empty.";
            return false;
        }

        var segments = pattern.Split('/');
        var subSegments = new List<string>(segments.Length);
        var captureNames = new List<string>();
        var hasMulti = false;

        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (hasMulti)
            {
                error = "Multi-level wildcard must be the last segment in the topic.";
                return false;
            }

            if (seg.StartsWith('{') && seg.EndsWith('}'))
            {
                var placeholder = seg[1..^1];
                if (string.IsNullOrWhiteSpace(placeholder))
                {
                    error = "Placeholder name cannot be empty.";
                    return false;
                }

                if (placeholder.EndsWith('*'))
                {
                    if (i != segments.Length - 1)
                    {
                        error = "Multi-level placeholder must be the last segment in the topic.";
                        return false;
                    }
                    hasMulti = true;
                    var name = placeholder[..^1];
                    captureNames.Add(name);
                    subSegments.Add("#");
                }
                else
                {
                    captureNames.Add(placeholder);
                    subSegments.Add("+");
                }
            }
            else if (seg == "+")
            {
                subSegments.Add("+");
            }
            else if (seg == "#")
            {
                if (i != segments.Length - 1)
                {
                    error = "Multi-level wildcard '#' must be the last segment.";
                    return false;
                }
                hasMulti = true;
                subSegments.Add("#");
            }
            else
            {
                if (seg.Contains('+') || seg.Contains('#'))
                {
                    error = "Wildcard characters '+' and '#' must occupy an entire segment.";
                    return false;
                }
                subSegments.Add(seg);
            }
        }

        var filter = string.Join('/', subSegments);
        result = new TopicPattern(pattern, segments, filter, captureNames, hasMulti);
        return true;
    }

    public bool TryMatch(string topic, out Dictionary<string, string>? captures)
    {
        captures = null;
        var topicSegments = topic.Split('/');

        if (!_hasMultiWildcard && topicSegments.Length != _segments.Length) return false;
        if (_hasMultiWildcard && topicSegments.Length < _segments.Length - 1) return false;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < _segments.Length; i++)
        {
            var seg = _segments[i];
            if (seg.StartsWith('{') && seg.EndsWith('}'))
            {
                var placeholder = seg[1..^1];
                if (placeholder.EndsWith('*'))
                {
                    var name = placeholder[..^1];
                    var rest = string.Join('/', topicSegments.Skip(i));
                    result[name] = rest;
                    captures = result;
                    return true;
                }
                else
                {
                    result[placeholder] = topicSegments[i];
                }
            }
            else if (seg == "+")
            {
            }
            else if (seg == "#")
            {
                captures = result;
                return true;
            }
            else
            {
                if (!string.Equals(seg, topicSegments[i], StringComparison.Ordinal)) return false;
            }
        }

        captures = result;
        return true;
    }
}
