using System.Text;
using System.Text.Json;
using Watchpoint.Model.Documents;
using Watchpoint.Model.Documents.Library;

namespace Watchpoint.Wptypes;

/// <summary>Command line of <c>wptypes</c> (spec SCM §8). Exit codes: 0 no errors, 1 errors, 2 usage or I/O error.</summary>
internal static class WptypesCli
{
    public const int Ok = 0;
    public const int Errors = 1;
    public const int Usage = 2;

    public const string Help = """
        wptypes validate <folder> [--with <folder>...] [--strict-i18n] [--format text|json]
        wptypes doc <folder> [--with <folder>...] --out <file.md>
        wptypes gen-ts <folder> [--with <folder>...] --out <dir>
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length < 2 || args[0] is not ("validate" or "doc" or "gen-ts"))
        {
            error.WriteLine(Help);
            return Usage;
        }

        if (!Options.TryParse(args, out var options, out var problem))
        {
            error.WriteLine(problem);
            error.WriteLine(Help);
            return Usage;
        }

        LibrarySet set;
        try
        {
            var reader = DocumentReaderFactory.FromEmbeddedSchemas();
            set = new LibrarySet(LibraryLoader.Load(options.Folder, reader), [.. options.With.Select(w => LibraryLoader.Load(w, reader))]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"wptypes: {ex.Message}");
            return Usage;
        }

        try
        {
            return args[0] switch
            {
                "validate" => Validate(set, options, output),
                "doc" => Write(options.Out!, MarkdownCatalogue.Generate(set), output),
                _ => WriteTypeScript(set, options.Out!, output),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"wptypes: {ex.Message}");
            return Usage;
        }
    }

    private static int Validate(LibrarySet set, Options options, TextWriter output)
    {
        var report = new LibraryValidator().Validate(set, new LibraryValidationOptions(options.StrictI18n));
        if (options.Json)
        {
            output.WriteLine(ToJson(report));
        }
        else
        {
            foreach (var problem in report.Problems)
            {
                output.WriteLine(problem.ToString());
            }

            var errors = report.Problems.Count(p => p.Severity == ProblemSeverity.Error);
            output.WriteLine($"{set.Primary.Root}: {set.Primary.Capabilities.Count} capabilities, {set.Primary.Types.Count} types, " +
                $"{errors} errors, {report.Problems.Count - errors} warnings");
        }

        return report.HasErrors ? Errors : Ok;
    }

    private static int Write(string file, string content, TextWriter output)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(file, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        output.WriteLine($"wrote {file}");
        return Ok;
    }

    private static int WriteTypeScript(LibrarySet set, string dir, TextWriter output) =>
        Write(Path.Combine(dir, TypeScriptGenerator.FileName), TypeScriptGenerator.Generate(set), output);

    internal static string ToJson(LibraryValidationReport report)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("errors", report.Problems.Count(p => p.Severity == ProblemSeverity.Error));
            json.WriteNumber("warnings", report.Problems.Count(p => p.Severity == ProblemSeverity.Warning));
            json.WriteStartArray("problems");
            foreach (var p in report.Problems)
            {
                json.WriteStartObject();
                json.WriteString("severity", p.Severity == ProblemSeverity.Error ? "error" : "warning");
                json.WriteString("file", p.SourcePath);
                json.WriteString("path", p.JsonPath);
                json.WriteString("code", p.Code.Value);
                json.WriteStartObject("args");
                foreach (var (key, value) in p.Args)
                {
                    WriteValue(json, key, value);
                }

                json.WriteEndObject();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter json, string key, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNull(key);
                break;
            case int i:
                json.WriteNumber(key, i);
                break;
            case long l:
                json.WriteNumber(key, l);
                break;
            case IEnumerable<string> list when value is not string:
                json.WriteStartArray(key);
                foreach (var item in list)
                {
                    json.WriteStringValue(item);
                }

                json.WriteEndArray();
                break;
            default:
                json.WriteString(key, value.ToString());
                break;
        }
    }

    private sealed record Options(string Folder, IReadOnlyList<string> With, bool StrictI18n, bool Json, string? Out)
    {
        public static bool TryParse(string[] args, out Options options, out string problem)
        {
            options = new Options(args[1], [], false, false, null);
            problem = string.Empty;
            var with = new List<string>();
            for (var i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--with" when i + 1 < args.Length:
                        with.Add(args[++i]);
                        break;
                    case "--strict-i18n":
                        options = options with { StrictI18n = true };
                        break;
                    case "--format" when i + 1 < args.Length && args[i + 1] is "text" or "json":
                        options = options with { Json = args[++i] == "json" };
                        break;
                    case "--out" when i + 1 < args.Length:
                        options = options with { Out = args[++i] };
                        break;
                    default:
                        problem = $"wptypes: unexpected argument '{args[i]}'";
                        return false;
                }
            }

            options = options with { With = with };
            if (args[0] is "doc" or "gen-ts" && options.Out is null)
            {
                problem = $"wptypes {args[0]}: --out is required";
                return false;
            }

            if (!Directory.Exists(options.Folder))
            {
                problem = $"wptypes: folder not found: {options.Folder}";
                return false;
            }

            var missing = with.FirstOrDefault(w => !Directory.Exists(w));
            if (missing is not null)
            {
                problem = $"wptypes: folder not found: {missing}";
                return false;
            }

            return true;
        }
    }
}
