using System.Text.Json;
using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Model.Documents;
using Watchpoint.Model.Documents.Library;

namespace Watchpoint.Model.Internal;

/// <summary>Reads embedded standard library documents from assembly resources (spec MOD §5.1).</summary>
internal static class StandardLibraryLoader
{
    private const string ResourcePrefix = "types/";

    public static Result<LoadedLibrary> Load(IDocumentReader reader, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(logger);

        var assembly = typeof(StandardLibraryLoader).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToList();

        var capabilities = new List<SourceDocument<CapabilityDocument>>();
        var types = new List<SourceDocument<DeviceTypeDocument>>();
        var bundles = new Dictionary<string, SourceDocument<JsonElement>>(StringComparer.Ordinal);
        var symbolFiles = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
        var problems = new List<DocumentProblem>();
        var tempDir = Path.Combine(Path.GetTempPath(), "wp_std_symbols_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        foreach (var name in resourceNames)
        {
            var relative = name[ResourcePrefix.Length..];
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();

            if (relative.StartsWith("capabilities/", StringComparison.Ordinal) && relative.EndsWith(".json", StringComparison.Ordinal))
            {
                var res = reader.ReadCapability(bytes, relative);
                if (res.TryGetValue(out var cap, out var err))
                {
                    capabilities.Add(new SourceDocument<CapabilityDocument>(relative, cap));
                }
                else
                {
                    problems.Add(new DocumentProblem(ProblemSeverity.Error, relative, string.Empty, err.Code, err.Args));
                }
            }
            else if (relative.StartsWith("device-types/", StringComparison.Ordinal) && relative.EndsWith(".json", StringComparison.Ordinal))
            {
                var res = reader.ReadDeviceType(bytes, relative);
                if (res.TryGetValue(out var dt, out var err))
                {
                    types.Add(new SourceDocument<DeviceTypeDocument>(relative, dt));
                }
                else
                {
                    problems.Add(new DocumentProblem(ProblemSeverity.Error, relative, string.Empty, err.Code, err.Args));
                }
            }
            else if (relative.StartsWith("i18n/", StringComparison.Ordinal) && relative.EndsWith(".json", StringComparison.Ordinal))
            {
                var culture = Path.GetFileNameWithoutExtension(relative);
                try
                {
                    using var doc = JsonDocument.Parse(bytes);
                    bundles[culture] = new SourceDocument<JsonElement>(relative, doc.RootElement.Clone());
                }
                catch (JsonException ex)
                {
                    problems.Add(new DocumentProblem(ProblemSeverity.Error, relative, string.Empty, ErrorCode.FromTrusted("model.document.invalid"), new Dictionary<string, object?> { ["reason"] = ex.Message }));
                }
            }
            else if (relative.StartsWith("symbols/", StringComparison.Ordinal) && relative.EndsWith(".svg", StringComparison.Ordinal))
            {
                var parts = relative.Split('/');
                if (parts.Length >= 3)
                {
                    var symbolId = parts[1];
                    var stateName = Path.GetFileNameWithoutExtension(parts[2]);
                    if (!symbolFiles.TryGetValue(symbolId, out var states))
                    {
                        states = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                        symbolFiles[symbolId] = states;
                    }

                    states[stateName] = bytes;

                    var diskPath = Path.Combine(tempDir, $"{symbolId}_{stateName}.svg");
                    File.WriteAllBytes(diskPath, bytes);
                }
            }
        }

        var folder = new LibraryFolder
        {
            Root = tempDir,
            Capabilities = capabilities,
            Types = types,
            Bundles = bundles,
            Symbols = symbolFiles.ToDictionary(k => k.Key, k => (IReadOnlyDictionary<string, string>)k.Value.ToDictionary(x => x.Key, x => Path.Combine(tempDir, $"{k.Key}_{x.Key}.svg"), StringComparer.Ordinal), StringComparer.Ordinal),
            Manifest = null,
            LoadProblems = problems,
        };

        var validator = new LibraryValidator();
        var validationReport = validator.Validate(new LibrarySet(folder, []), new LibraryValidationOptions { StrictI18n = false });
        if (problems.Count > 0 || validationReport.Problems.Any(p => p.Severity == ProblemSeverity.Error))
        {
            var allErrors = problems.Concat(validationReport.Problems.Where(p => p.Severity == ProblemSeverity.Error)).ToList();
            var problemsSummary = string.Join("; ", allErrors.Select(p => $"{p.SourcePath}: {p.Code.Value}"));
            ModelLog.StandardLibraryInvalid(logger, problemsSummary);
            return Error.Create(ErrorKind.Internal, "model.standard_library.invalid", ("problems", problemsSummary));
        }

        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch
        {
        }

        var symbolsMap = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        foreach (var (symId, states) in symbolFiles)
        {
            foreach (var (state, bytes) in states)
            {
                symbolsMap[$"{symId}/{state}.svg"] = bytes;
            }
        }

        return new LoadedLibrary(capabilities, types, bundles, symbolsMap);
    }
}

internal sealed record LoadedLibrary(
    IReadOnlyList<SourceDocument<CapabilityDocument>> Capabilities,
    IReadOnlyList<SourceDocument<DeviceTypeDocument>> Types,
    IReadOnlyDictionary<string, SourceDocument<JsonElement>> Bundles,
    IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Symbols);
