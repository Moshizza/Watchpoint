using System.Text.Json;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Model.Documents.Library;

/// <summary>Severity of a <see cref="DocumentProblem"/>.</summary>
public enum ProblemSeverity
{
    /// <summary>Accepted, reported.</summary>
    Warning,

    /// <summary>The library is invalid.</summary>
    Error,
}

/// <summary>A problem found in a document of a library (spec SCM §7).</summary>
/// <param name="Severity">Error or warning.</param>
/// <param name="SourcePath">File, relative to the library folder.</param>
/// <param name="JsonPath">JSON Pointer inside the file (empty for the whole file).</param>
/// <param name="Code">Problem code, e.g. <c>scm.capability.missing</c>.</param>
/// <param name="Args">Arguments of the message <c>errors.&lt;code&gt;</c>.</param>
public sealed record DocumentProblem(ProblemSeverity Severity, string SourcePath, string JsonPath, ErrorCode Code,
    IReadOnlyDictionary<string, object?> Args)
{
    /// <summary>Formats the problem for a console: <c>error file#/path code key=value…</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() =>
        $"{(Severity == ProblemSeverity.Error ? "error" : "warning")} {SourcePath}{(JsonPath.Length > 0 ? "#" + JsonPath : string.Empty)} {Code}"
        + string.Concat(Args.Select(a => $" {a.Key}={Format(a.Value)}"));

    private static string Format(object? value) => value switch
    {
        null => "null",
        IEnumerable<string> list when value is not string => "[" + string.Join(",", list) + "]",
        _ => value.ToString() ?? string.Empty,
    };
}

/// <summary>Result of a library validation.</summary>
/// <param name="Problems">The problems, ordered by file and path.</param>
public sealed record LibraryValidationReport(IReadOnlyList<DocumentProblem> Problems)
{
    /// <summary>Gets whether at least one problem is an error.</summary>
    public bool HasErrors => Problems.Any(p => p.Severity == ProblemSeverity.Error);
}

/// <summary>Options of a library validation.</summary>
/// <param name="StrictI18n">Missing <c>fr</c>/<c>ar</c> keys are errors instead of warnings.</param>
public sealed record LibraryValidationOptions(bool StrictI18n = false);

/// <summary>A document and the file it came from.</summary>
/// <typeparam name="T">The document type.</typeparam>
/// <param name="SourcePath">File, relative to its library folder.</param>
/// <param name="Document">The parsed document.</param>
public sealed record SourceDocument<T>(string SourcePath, T Document);

/// <summary>The content of one library folder: the standard library (<c>types/</c>) or a package.</summary>
public sealed record LibraryFolder
{
    /// <summary>Gets the folder path.</summary>
    public required string Root { get; init; }

    /// <summary>Gets the capabilities.</summary>
    public IReadOnlyList<SourceDocument<CapabilityDocument>> Capabilities { get; init; } = [];

    /// <summary>Gets the device and server types.</summary>
    public IReadOnlyList<SourceDocument<DeviceTypeDocument>> Types { get; init; } = [];

    /// <summary>Gets the translation bundles by culture (<c>en</c>, <c>fr</c>, <c>ar</c>), with their file.</summary>
    public IReadOnlyDictionary<string, SourceDocument<JsonElement>> Bundles { get; init; } = new Dictionary<string, SourceDocument<JsonElement>>();

    /// <summary>Gets the symbol files: symbol id → state (<c>default</c> included) → file path (absolute).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Symbols { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    /// <summary>Gets the manifest, for a package folder.</summary>
    public SourceDocument<ManifestDocument>? Manifest { get; init; }

    /// <summary>Gets the problems found while reading the folder (unreadable or invalid documents).</summary>
    public IReadOnlyList<DocumentProblem> LoadProblems { get; init; } = [];
}

/// <summary>What is validated: one primary folder, with the folders it depends on (e.g. a package with the standard library).</summary>
/// <param name="Primary">The folder whose documents are validated.</param>
/// <param name="Dependencies">Folders providing capabilities, types, translations and symbols.</param>
public sealed record LibrarySet(LibraryFolder Primary, IReadOnlyList<LibraryFolder> Dependencies)
{
    /// <summary>Gets all folders, primary first.</summary>
    public IEnumerable<LibraryFolder> All => Dependencies.Prepend(Primary);
}

/// <summary>Cross-document checks over a set of folders (spec SCM §7).</summary>
public interface ILibraryValidator
{
    /// <summary>Validates the primary folder of <paramref name="library"/>.</summary>
    /// <param name="library">The folders.</param>
    /// <param name="options">The options.</param>
    /// <returns>The report.</returns>
    LibraryValidationReport Validate(LibrarySet library, LibraryValidationOptions options);
}

/// <summary>A type with its <c>extends</c> chain applied (spec SCM §5.1).</summary>
/// <param name="Document">The type's own document.</param>
/// <param name="Capabilities">Capabilities of the chain, parents first; a child's use of the same capability replaces the parent's.</param>
/// <param name="AttributeNames">Attribute names of the chain.</param>
/// <param name="Display">The child's display.</param>
/// <param name="LicenceClass">The child's licence class.</param>
/// <param name="DefaultAlarmRules">The child's rules if it declares any, else the parent's.</param>
/// <param name="StateOverrides">Overrides of the chain, child first.</param>
/// <param name="Chain">Type ids from the child to the root.</param>
public sealed record ResolvedType(
    DeviceTypeDocument Document,
    IReadOnlyList<CapabilityUse> Capabilities,
    IReadOnlyList<string> AttributeNames,
    TypeDisplay Display,
    LicenceClass LicenceClass,
    IReadOnlyList<string> DefaultAlarmRules,
    IReadOnlyList<StateSummaryOverride> StateOverrides,
    IReadOnlyList<TypeId> Chain);
