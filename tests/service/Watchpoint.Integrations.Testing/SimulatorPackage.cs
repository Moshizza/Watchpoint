using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Watchpoint.Integrations.Testing;

/// <summary>
/// Stages the simulator package (spec C-SIM §3) the way <c>wppack</c> will: the build output of
/// <c>Watchpoint.Integration.Simulator</c> (manifest, types, i18n, settings, docs, assemblies with the deps.json),
/// a <c>files</c> list with SHA-256 hashes, and a signature. Shared contract assemblies are not shipped.
/// </summary>
public static class SimulatorPackage
{
    /// <summary>The package id.</summary>
    public const string Id = "watchpoint.integration.simulator";

    private const string MainAssembly = "Watchpoint.Integration.Simulator";
    private static readonly string[] _contentFolders = ["types", "i18n", "settings", "docs"];
    private static readonly string[] _privateAssemblies = [MainAssembly, "Watchpoint.Integration.Simulator.Engine"];

    /// <summary>Writes the package as an unpacked folder.</summary>
    /// <param name="buildOutput">Folder holding the simulator's build output (its <c>manifest.json</c> and assemblies).</param>
    /// <param name="target">The folder to write (created).</param>
    /// <param name="key">The signing key.</param>
    /// <returns><paramref name="target"/>.</returns>
    public static string Stage(string buildOutput, string target, TestKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var folder in _contentFolders)
        {
            var source = Path.Combine(buildOutput, folder);
            if (!Directory.Exists(source))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                files[$"{folder}/{Path.GetRelativePath(source, file).Replace('\\', '/')}"] = File.ReadAllBytes(file);
            }
        }

        foreach (var assembly in _privateAssemblies)
        {
            files[$"lib/{assembly}.dll"] = File.ReadAllBytes(Path.Combine(buildOutput, assembly + ".dll"));
        }

        var deps = Path.Combine(buildOutput, MainAssembly + ".deps.json");
        if (File.Exists(deps))
        {
            files[$"lib/{MainAssembly}.deps.json"] = File.ReadAllBytes(deps);
        }

        var manifest = JsonNode.Parse(File.ReadAllBytes(Path.Combine(buildOutput, "manifest.json")))!.AsObject();
        manifest["files"] = new JsonArray([.. files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => (JsonNode)new JsonObject
        {
            ["path"] = f.Key,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(f.Value)),
        })]);
        var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        files["manifest.json"] = manifestBytes;
        files["signature.sig"] = Encoding.UTF8.GetBytes(key.SignatureFile(manifestBytes));

        foreach (var (path, content) in files)
        {
            var file = Path.Combine(target, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, content);
        }

        return target;
    }
}
