using System.Security.Cryptography;
using System.Text;
using Watchpoint.Model.Documents.Library;

namespace Watchpoint.Model.Documents.Tests.Library;

/// <summary>A library folder written to a temporary directory for one test.</summary>
internal sealed class TempLibrary : IDisposable
{
    public const string CapabilitySchema = "https://schemas.watchpoint.dev/capability/1.json";
    public const string TypeSchema = "https://schemas.watchpoint.dev/device-type/1.json";

    public TempLibrary() => Directory.CreateDirectory(Root);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "wp-lib-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));

    /// <summary>The repository's standard library (types/).</summary>
    public static LibraryFolder Standard { get; } = LibraryLoader.Load(FindRepoFolder("types"), DocumentReaderFactory.FromEmbeddedSchemas());

    public static string FindRepoFolder(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, name, "capabilities")) || File.Exists(Path.Combine(dir.FullName, name, ".marker")))
            {
                return Path.Combine(dir.FullName, name);
            }
        }

        throw new DirectoryNotFoundException(name);
    }

    public TempLibrary Capability(string id, string properties = "[]", string commands = "[]", string events = "[]", string version = "1.0.0", string? file = null) =>
        Write($"capabilities/{file ?? id}.json",
            $$"""{"$schema":"{{CapabilitySchema}}","id":"{{id}}","version":"{{version}}","properties":{{properties}},"commands":{{commands}},"events":{{events}}}""");

    public TempLibrary Type(string id, string capabilities = """[{"id":"health","version":"^1.0"}]""", string primary = "health.connectivity",
        string extends = "null", string extra = "", string symbol = "generic.server") =>
        Write($"device-types/{id}.json",
            $$"""{"$schema":"{{TypeSchema}}","id":"{{id}}","version":"1.0.0","role":"device","category":"generic","extends":{{extends}},"capabilities":{{capabilities}},"licenceClass":"full","display":{"icon":"box","symbol":"{{symbol}}","primaryProperty":"{{primary}}"}{{extra}}}""");

    public TempLibrary Write(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return this;
    }

    public LibraryFolder Load() => LibraryLoader.Load(Root, DocumentReaderFactory.FromEmbeddedSchemas());

    /// <summary>Validates this folder with the standard library as dependency.</summary>
    public LibraryValidationReport Validate(bool strictI18n = false, bool withStandard = true) =>
        new LibraryValidator().Validate(new LibrarySet(Load(), withStandard ? [Standard] : []), new LibraryValidationOptions(strictI18n));

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal static class ReportExtensions
{
    public static IEnumerable<DocumentProblem> WithCode(this LibraryValidationReport report, string code) =>
        report.Problems.Where(p => p.Code.Value == code);
}
