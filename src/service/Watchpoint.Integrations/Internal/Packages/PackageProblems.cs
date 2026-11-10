using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Model.Documents.Library;

namespace Watchpoint.Integrations.Internal.Packages;

/// <summary>Builds <see cref="DocumentProblem"/>s of the package checks.</summary>
internal static class PackageProblems
{
    public const string ManifestFile = "manifest.json";

    public static DocumentProblem Error(string code, string source, params (string Key, object? Value)[] args) =>
        new(ProblemSeverity.Error, source, string.Empty, ErrorCode.FromTrusted(code), Args(args));

    public static DocumentProblem Warning(string code, string source, params (string Key, object? Value)[] args) =>
        new(ProblemSeverity.Warning, source, string.Empty, ErrorCode.FromTrusted(code), Args(args));

    private static Dictionary<string, object?> Args((string Key, object? Value)[] args)
    {
        var map = new Dictionary<string, object?>(args.Length, StringComparer.Ordinal);
        foreach (var (key, value) in args)
        {
            map[key] = value;
        }

        return map;
    }

    /// <summary>The problems carried by a failed <see cref="Result"/>: one per field error, or one for the error itself.</summary>
    public static IEnumerable<DocumentProblem> FromError(Error error, string source) =>
        error.FieldErrors.Count == 0
            ? [new DocumentProblem(ProblemSeverity.Error, source, string.Empty, error.Code, error.Args)]
            : error.FieldErrors.Select(f => new DocumentProblem(ProblemSeverity.Error, source, f.Path, f.Code, f.Args));
}
