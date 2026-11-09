namespace Watchpoint.Expressions.Internal;

internal static class Suggestions
{
    public static int DamerauLevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source))
        {
            return string.IsNullOrEmpty(target) ? 0 : target.Length;
        }

        if (string.IsNullOrEmpty(target))
        {
            return source.Length;
        }

        var sourceLen = source.Length;
        var targetLen = target.Length;
        var matrix = new int[sourceLen + 1, targetLen + 1];

        for (var i = 0; i <= sourceLen; i++)
        {
            matrix[i, 0] = i;
        }

        for (var j = 0; j <= targetLen; j++)
        {
            matrix[0, j] = j;
        }

        for (var i = 1; i <= sourceLen; i++)
        {
            for (var j = 1; j <= targetLen; j++)
            {
                var cost = source[i - 1] == target[j - 1] ? 0 : 1;

                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);

                if (i > 1 && j > 1 && source[i - 1] == target[j - 2] && source[i - 2] == target[j - 1])
                {
                    matrix[i, j] = Math.Min(matrix[i, j], matrix[i - 2, j - 2] + cost);
                }
            }
        }

        return matrix[sourceLen, targetLen];
    }

    public static IReadOnlyList<string> FindSuggestions(string input, IEnumerable<string> candidates)
    {
        var result = new List<(string Candidate, int Distance)>();

        foreach (var candidate in candidates)
        {
            var dist = DamerauLevenshteinDistance(input, candidate);
            if (dist <= 2)
            {
                result.Add((candidate, dist));
            }
        }

        return result.OrderBy(r => r.Distance).Select(r => r.Candidate).Distinct(StringComparer.Ordinal).ToList();
    }
}
