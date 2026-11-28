using Shouldly;
using Watchpoint.Host;
using Xunit;

namespace Watchpoint.IntegrationTests.Api;

/// <summary>The committed OpenAPI document is the generated one (spec API §5.7).</summary>
[Trait("layer", "I")]
[Trait("spec", "API")]
public sealed class OpenApiDocumentTests
{
    [Fact]
    public async Task API_I_015_OpenAPI_document_generated_equals_committed_file()
    {
        var committed = Path.Combine(RepositoryRoot(), "src", "ui", "packages", "api-client", "openapi", "v1.json");
        File.Exists(committed).ShouldBeTrue("run: dotnet run --project tools/export-openapi");

        var generated = await OpenApiExport.GenerateAsync(TestContext.Current.CancellationToken);

        (await File.ReadAllTextAsync(committed, TestContext.Current.CancellationToken)).Replace("\r\n", "\n", StringComparison.Ordinal)
            .ShouldBe(generated, "the API changed: regenerate with `dotnet run --project tools/export-openapi` and commit the file");
        generated.ShouldContain("\"operationId\": \"Devices_ListDevices\"");
        generated.ShouldContain("\"x-wp-permission\": \"config.registry.edit\"");
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
