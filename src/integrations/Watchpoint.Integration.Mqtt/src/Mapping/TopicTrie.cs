namespace Watchpoint.Integration.Mqtt.Mapping;

public sealed class TopicTrie<T>
{
    private sealed class Node
    {
        public readonly Dictionary<string, Node> ExactChildren = new(StringComparer.Ordinal);
        public Node? SingleWildcardChild;
        public Node? MultiWildcardChild;
        public readonly List<T> Values = new();
    }

    private readonly Node _root = new();

    public void Add(TopicPattern pattern, T value)
    {
        var current = _root;
        for (var i = 0; i < pattern.Segments.Count; i++)
        {
            var seg = pattern.Segments[i];
            if (seg.StartsWith('{') && seg.EndsWith('}'))
            {
                var placeholder = seg[1..^1];
                if (placeholder.EndsWith('*'))
                {
                    current.MultiWildcardChild ??= new Node();
                    current = current.MultiWildcardChild;
                    break;
                }
                else
                {
                    current.SingleWildcardChild ??= new Node();
                    current = current.SingleWildcardChild;
                }
            }
            else if (seg == "+")
            {
                current.SingleWildcardChild ??= new Node();
                current = current.SingleWildcardChild;
            }
            else if (seg == "#")
            {
                current.MultiWildcardChild ??= new Node();
                current = current.MultiWildcardChild;
                break;
            }
            else
            {
                if (!current.ExactChildren.TryGetValue(seg, out var next))
                {
                    next = new Node();
                    current.ExactChildren[seg] = next;
                }
                current = next;
            }
        }

        current.Values.Add(value);
    }

    public List<T> Match(string topic)
    {
        var segments = topic.Split('/');
        var results = new List<T>();
        MatchRecursive(_root, segments, 0, results);
        return results;
    }

    private static void MatchRecursive(Node current, string[] segments, int index, List<T> results)
    {
        if (current.MultiWildcardChild != null)
        {
            results.AddRange(current.MultiWildcardChild.Values);
        }

        if (index == segments.Length)
        {
            results.AddRange(current.Values);
            return;
        }

        var segment = segments[index];

        if (current.ExactChildren.TryGetValue(segment, out var nextExact))
        {
            MatchRecursive(nextExact, segments, index + 1, results);
        }

        if (current.SingleWildcardChild != null)
        {
            MatchRecursive(current.SingleWildcardChild, segments, index + 1, results);
        }
    }
}
