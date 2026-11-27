using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Watchpoint.Host;

/// <summary>Generates the OpenAPI document of the composed host (spec API §5.7, API-I-015).</summary>
public static class OpenApiExport
{
    /// <summary>The document as indented JSON (OpenAPI 3.1), with a final newline.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The JSON text.</returns>
    public static async Task<string> GenerateAsync(CancellationToken ct)
    {
        await using var app = Program.ComposeForInspection();

        // The endpoints reach the global endpoint data source (read by ApiExplorer) when the routing pipeline is built.
        app.UseRouting();
        app.UseEndpoints(_ => { });
        var provider = app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync(ct).ConfigureAwait(false);
        using var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        document.SerializeAsV31(new OpenApiJsonWriter(text));
        return text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }
}
