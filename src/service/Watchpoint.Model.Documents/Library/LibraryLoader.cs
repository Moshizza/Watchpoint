using System.Text.Json;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Model.Documents.Library;

/// <summary>Reads a library folder: the standard library layout (<c>capabilities/</c>, <c>device-types/</c>, <c>symbols/</c>, <c>i18n/</c>) or a package (<c>manifest.json</c>).</summary>
public static class LibraryLoader
{
    /// <summary>Maximum document size (spec SCM §9).</summary>
    public const int MaxDocumentBytes = 1024 * 1024;

    /// <summary>The cultures every library translates.</summary>
    public static readonly IReadOnlyList<string> Cultures = ["en", "fr", "ar"];

    /// <summary>Reads <paramref name="root"/>; unreadable documents become <see cref="LibraryFolder.LoadProblems"/>.</summary>
    /// <param name="root">The folder.</param>
    /// <param name="reader">The document reader.</param>
    /// <returns>The folder content.</returns>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    public static LibraryFolder Load(string root, IDocumentReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Library folder not found: {root}");
        }

        var problems = new List<DocumentProblem>();
        SourceDocument<ManifestDocument>? manifest = null;
        IEnumerable<string> capabilityFiles;
        IEnumerable<string> typeFiles;
        string symbols;
        string i18n;

        var manifestPath = Path.Combine(root, "manifest.json");
        if (File.Exists(manifestPath))
        {
            var doc = Read(root, manifestPath, problems, reader.ReadManifest);
            manifest = doc;
            capabilityFiles = doc?.Document.Capabilities.Select(p => Path.Combine(root, p)) ?? [];
            typeFiles = doc?.Document.Types.Select(p => Path.Combine(root, p)) ?? [];
            symbols = Path.Combine(root, doc?.Document.Symbols ?? "symbols");
            i18n = Path.Combine(root, doc?.Document.I18n ?? "i18n");
        }
        else
        {
            capabilityFiles = JsonFiles(Path.Combine(root, "capabilities"));
            typeFiles = JsonFiles(Path.Combine(root, "device-types"));
            symbols = Path.Combine(root, "symbols");
            i18n = Path.Combine(root, "i18n");
        }

        var capabilities = capabilityFiles.Select(f => Read(root, f, problems, reader.ReadCapability)).OfType<SourceDocument<CapabilityDocument>>().ToList();
        var types = typeFiles.Select(f => Read(root, f, problems, reader.ReadDeviceType)).OfType<SourceDocument<DeviceTypeDocument>>().ToList();

        var bundles = new Dictionary<string, SourceDocument<JsonElement>>(StringComparer.Ordinal);
        foreach (var file in JsonFiles(i18n))
        {
            var relative = Relative(root, file);
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllBytes(file));
                bundles[Path.GetFileNameWithoutExtension(file)] = new SourceDocument<JsonElement>(relative, json.RootElement.Clone());
            }
            catch (JsonException ex)
            {
                problems.Add(Invalid(relative, ex.Message));
            }
        }

        var symbolFiles = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (Directory.Exists(symbols))
        {
            foreach (var dir in Directory.EnumerateDirectories(symbols).Order(StringComparer.Ordinal))
            {
                symbolFiles[Path.GetFileName(dir)] = Directory.EnumerateFiles(dir, "*.svg")
                    .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => f, StringComparer.Ordinal);
            }
        }

        return new LibraryFolder
        {
            Root = root,
            Capabilities = capabilities,
            Types = types,
            Bundles = bundles,
            Symbols = symbolFiles,
            Manifest = manifest,
            LoadProblems = problems,
        };
    }

    /// <summary>Path relative to the library folder, with forward slashes.</summary>
    /// <param name="root">The folder.</param>
    /// <param name="file">The file.</param>
    /// <returns>The relative path.</returns>
    public static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private delegate Result<T> ReadDocument<T>(ReadOnlySpan<byte> utf8, string sourcePath);

    private static SourceDocument<T>? Read<T>(string root, string file, List<DocumentProblem> problems, ReadDocument<T> read)
    {
        var relative = Relative(root, file);
        if (!File.Exists(file))
        {
            problems.Add(new DocumentProblem(ProblemSeverity.Error, relative, string.Empty, ErrorCode.FromTrusted(ProblemCodes.ManifestFileMissing),
                new Dictionary<string, object?> { ["file"] = relative }));
            return null;
        }

        var bytes = File.ReadAllBytes(file);
        if (bytes.Length > MaxDocumentBytes)
        {
            problems.Add(new DocumentProblem(ProblemSeverity.Error, relative, string.Empty, CommonErrorCodes.ValueTooLong,
                new Dictionary<string, object?> { ["max"] = MaxDocumentBytes }));
            return null;
        }

        var result = read(bytes, relative);
        if (result.TryGetValue(out var document, out var error))
        {
            return new SourceDocument<T>(relative, document);
        }

        if (error.FieldErrors.Count == 0)
        {
            problems.Add(new DocumentProblem(ProblemSeverity.Error, relative, string.Empty, error.Code, error.Args));
        }

        foreach (var field in error.FieldErrors)
        {
            problems.Add(new DocumentProblem(ProblemSeverity.Error, relative, field.Path, field.Code, field.Args));
        }

        return null;
    }

    private static DocumentProblem Invalid(string file, string reason) =>
        new(ProblemSeverity.Error, file, string.Empty, ErrorCode.FromTrusted(ProblemCodes.DocumentInvalid), new Dictionary<string, object?> { ["reason"] = reason });

    private static IEnumerable<string> JsonFiles(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal) : [];
}
