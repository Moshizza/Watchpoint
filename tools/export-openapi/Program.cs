using Watchpoint.Host;

namespace Watchpoint.ExportOpenApi;

/// <summary>
/// <c>dotnet run --project tools/export-openapi [-- &lt;output path&gt;]</c>: composes the host's modules (nothing started),
/// generates the OpenAPI document <c>v1</c> and writes it (default <c>src/ui/packages/api-client/openapi/v1.json</c>).
/// </summary>
public static class Program
{
    /// <summary>The committed document, relative to the repository root.</summary>
    public const string DefaultOutput = "src/ui/packages/api-client/openapi/v1.json";

    /// <summary>Entry point.</summary>
    /// <param name="args">Optional output path.</param>
    /// <returns>Exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var output = args.Length > 0 ? args[0] : Path.Combine(FindRepositoryRoot(), DefaultOutput);
        var json = await OpenApiExport.GenerateAsync(CancellationToken.None).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, json).ConfigureAwait(false);
        await Console.Out.WriteLineAsync("OpenAPI document written to " + output).ConfigureAwait(false);
        return 0;
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Run from inside the repository (global.json not found).");
    }
}
