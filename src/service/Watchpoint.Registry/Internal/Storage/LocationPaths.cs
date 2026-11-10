namespace Watchpoint.Registry.Internal.Storage;

internal static class LocationPaths
{
    public const string Separator = "/";

    public static string BuildPath(string? parentPath, string segment)
    {
        if (string.IsNullOrEmpty(parentPath))
            return segment;

        return parentPath.EndsWith(Separator, StringComparison.Ordinal)
            ? parentPath + segment
            : parentPath + Separator + segment;
    }

    public static string RewriteDescendantPath(string currentPath, string oldParentPath, string newParentPath)
    {
        if (!currentPath.StartsWith(oldParentPath, StringComparison.Ordinal))
            return currentPath;

        var remainder = currentPath.Substring(oldParentPath.Length);
        if (remainder.StartsWith(Separator, StringComparison.Ordinal))
            remainder = remainder.Substring(1);

        return BuildPath(newParentPath, remainder);
    }

    public static bool IsDescendantOf(string candidatePath, string ancestorPath)
    {
        if (candidatePath == ancestorPath)
            return false;

        return candidatePath.StartsWith(ancestorPath.EndsWith(Separator, StringComparison.Ordinal) ? ancestorPath : ancestorPath + Separator, StringComparison.Ordinal);
    }
}
