using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Xunit;

namespace Watchpoint.IntegrationTests.Api;

/// <summary>Every registered error code has a text in the UI bundles (spec API §11, UIC: <c>errors.&lt;code&gt;</c>).</summary>
[Trait("layer", "I")]
[Trait("spec", "API")]
public sealed partial class ErrorTranslationTests
{
    private static readonly string[] _cultures = ["en", "fr", "ar"];

    /// <summary>Modules whose <c>*Errors</c> classes must be found; guards against an assembly silently dropping out of the scan.</summary>
    private static readonly string[] _expectedPrefixes =
        ["api.", "commands.", "common.", "config.", "expr.", "history.", "host.", "identity.", "irt.", "model.", "pipeline.", "registry.", "scheduling.", "secrets.", "state.", "storage."];

    [Fact]
    public void API_I_014_Error_codes_have_errors_keys_in_en_fr_ar_bundles()
    {
        var registered = RegisteredCodes();
        var codes = registered.Keys.Order(StringComparer.Ordinal).ToList();
        foreach (var prefix in _expectedPrefixes)
        {
            codes.ShouldContain(c => c.StartsWith(prefix, StringComparison.Ordinal), $"no error code starting with {prefix} was found");
        }

        codes.ShouldContain("api.idempotency.mismatch");
        codes.ShouldContain("registry.device.not_found");

        var missing = new List<string>();
        var unknownArgs = new List<string>();
        foreach (var culture in _cultures)
        {
            var bundle = JsonNode.Parse(File.ReadAllText(Path.Combine(ApiNodeFixture.RepositoryRoot(), "src", "ui", "packages", "i18n", "locales", culture, "errors.json")))!;
            foreach (var code in codes)
            {
                if (Lookup(bundle, code) is not JsonValue text)
                {
                    missing.Add(culture + ": errors." + code);
                    continue;
                }

                // Placeholders must name arguments the server sends (only known for codes built by a factory).
                if (registered[code] is { } args)
                {
                    unknownArgs.AddRange(Placeholder().Matches(text.GetValue<string>()).Select(m => m.Groups[1].Value)
                        .Where(name => !args.Contains(name)).Select(name => $"{culture}: errors.{code} uses {{{name}}}"));
                }
            }
        }

        missing.ShouldBeEmpty();
        unknownArgs.ShouldBeEmpty();
    }

    /// <summary>
    /// The codes of every public <c>*Errors</c> class in the service assemblies (<c>src/service/*</c>): code constants
    /// (<see cref="string"/> or <see cref="ErrorCode"/>) and the codes produced by the static factories, field errors
    /// included, mapped to the argument names the factories give them (<see langword="null"/> when only a constant is known).
    /// Factories are invoked with sample arguments.
    /// </summary>
    private static Dictionary<string, HashSet<string>?> RegisteredCodes()
    {
        var codes = new Dictionary<string, HashSet<string>?>(StringComparer.Ordinal);
        foreach (var type in ServiceAssemblies().SelectMany(a => a.GetExportedTypes())
                     .Where(t => t is { IsAbstract: true, IsSealed: true } && t.Name.EndsWith("Errors", StringComparison.Ordinal)))
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                switch (field.GetValue(null))
                {
                    case string s when CodePattern().IsMatch(s):
                        codes.TryAdd(s, null);
                        break;
                    case ErrorCode c:
                        codes.TryAdd(c.Value, null);
                        break;
                }
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => !m.IsGenericMethodDefinition))
            {
                if (method.ReturnType != typeof(Error) && method.ReturnType != typeof(FieldError))
                {
                    continue;
                }

                object? result;
                try
                {
                    result = method.Invoke(null, [.. method.GetParameters().Select(p => Sample(p.ParameterType))]);
                }
                catch (TargetInvocationException e)
                {
                    throw new InvalidOperationException($"{type.FullName}.{method.Name} could not be invoked with sample arguments.", e.InnerException);
                }

                switch (result)
                {
                    case Error error:
                        AddArgs(codes, error.Code, error.Args.Keys);
                        foreach (var field in error.FieldErrors)
                        {
                            AddArgs(codes, field.Code, field.Args.Keys);
                        }

                        break;
                    case FieldError fieldError:
                        AddArgs(codes, fieldError.Code, fieldError.Args.Keys);
                        break;
                }
            }
        }

        return codes;
    }

    private static void AddArgs(Dictionary<string, HashSet<string>?> codes, ErrorCode code, IEnumerable<string> args)
    {
        if (codes.TryGetValue(code.Value, out var known) && known is not null)
        {
            known.UnionWith(args);
        }
        else
        {
            codes[code.Value] = new HashSet<string>(args, StringComparer.Ordinal);
        }
    }

    private static IEnumerable<Assembly> ServiceAssemblies()
    {
        var projects = Directory.GetDirectories(Path.Combine(ApiNodeFixture.RepositoryRoot(), "src", "service")).Select(Path.GetFileName);
        foreach (var name in projects)
        {
            var path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            if (File.Exists(path))
            {
                yield return Assembly.Load(new AssemblyName(name!));
            }
        }
    }

    private static JsonNode? Lookup(JsonNode bundle, string code)
    {
        JsonNode? node = bundle;
        foreach (var part in code.Split('.'))
        {
            node = node is JsonObject o && o.TryGetPropertyValue(part, out var next) ? next : null;
        }

        return node;
    }

    private static object? Sample(Type type) => type switch
    {
        _ when type == typeof(string) => "x",
        _ when type == typeof(long) => 1L,
        _ when type == typeof(int) => 1,
        _ when type == typeof(Guid) => Guid.Parse("00000000-0000-0000-0000-000000000001"),
        _ when type == typeof(DateTimeOffset) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        _ when type == typeof(TimeSpan) => TimeSpan.FromSeconds(1),
        _ when type.IsArray => Array.CreateInstance(type.GetElementType()!, 0),
        _ when type.IsGenericType && type.GetGenericTypeDefinition() is var d
            && (d == typeof(IReadOnlyList<>) || d == typeof(IEnumerable<>) || d == typeof(IReadOnlyCollection<>)) =>
            Array.CreateInstance(type.GetGenericArguments()[0], 0),
        _ when type == typeof(object) => "x",
        _ when Nullable.GetUnderlyingType(type) is { } underlying => Sample(underlying),
        _ when type.IsEnum => Enum.GetValues(type).GetValue(0),
        _ => SampleFromFactory(type),
    };

    /// <summary>Ids and value objects: <c>New()</c>, <c>FromTrusted(string)</c> or a successful <c>Parse(string)</c>.</summary>
    private static object? SampleFromFactory(Type type)
    {
        const BindingFlags Statics = BindingFlags.Public | BindingFlags.Static;
        if (type.GetMethod("New", Statics, Type.EmptyTypes) is { } create && create.ReturnType == type)
        {
            return create.Invoke(null, null);
        }

        if (type.GetMethod("FromTrusted", Statics, [typeof(string)]) is { } trusted && trusted.ReturnType == type)
        {
            return trusted.Invoke(null, ["sample"]);
        }

        if (type.GetMethod("Parse", Statics, [typeof(string)]) is { } parse)
        {
            foreach (var text in new[] { "1.0.0", "sample" })
            {
                var result = parse.Invoke(null, [text])!;
                if (result.GetType().GetProperty("IsSuccess")?.GetValue(result) is true)
                {
                    return result.GetType().GetProperty("Value")!.GetValue(result);
                }
            }
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    [GeneratedRegex("^[a-z0-9_]+(\\.[a-z0-9_]+)+$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("\\{\\s*([A-Za-z_][A-Za-z0-9_]*)\\s*[,}]")]
    private static partial Regex Placeholder();
}
