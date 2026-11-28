using System.Reflection;
using System.Text.Json.Nodes;
using Shouldly;
using Watchpoint.Api.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Xunit;

namespace Watchpoint.IntegrationTests.Api;

/// <summary>Error codes of the framework have texts in the UI bundles (spec API §11, UIC: <c>errors.&lt;code&gt;</c>).</summary>
[Trait("layer", "I")]
[Trait("spec", "API")]
public sealed class ErrorTranslationTests
{
    [Fact]
    public void API_I_014_Error_codes_have_errors_keys_in_en_fr_ar_bundles()
    {
        // Scope (T1.19): the codes the framework registers — Kernel common codes and the API's own. Module codes are
        // checked by their modules' tasks against the same bundles.
        var codes = typeof(CommonErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(ErrorCode))
            .Select(f => ((ErrorCode)f.GetValue(null)!).Value)
            .Concat(typeof(ApiErrors).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.ReturnType == typeof(Error))
                .Select(m => ((Error)m.Invoke(null, [.. m.GetParameters().Select(Sample)])!).Code.Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        codes.ShouldContain("api.idempotency.mismatch");

        var missing = new List<string>();
        foreach (var culture in new[] { "en", "fr", "ar" })
        {
            var bundle = JsonNode.Parse(File.ReadAllText(Path.Combine(ApiNodeFixture.RepositoryRoot(), "src", "ui", "packages", "i18n", "locales", culture, "errors.json")))!;
            missing.AddRange(codes.Where(code => Lookup(bundle, code) is not JsonValue).Select(code => culture + ": errors." + code));
        }

        missing.ShouldBeEmpty();
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

    private static object? Sample(ParameterInfo parameter) => parameter.ParameterType switch
    {
        var t when t == typeof(string) => "x",
        var t when t == typeof(long) => 1L,
        var t when t == typeof(int) => 1,
        _ => null,
    };
}
