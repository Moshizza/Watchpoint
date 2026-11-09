using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Watchpoint.Integration.Simulator.Tests;

public static class PackageStager
{
    public static string Stage(string sourcePackageDir, string assemblyLocation)
    {
        var tempDir = Directory.CreateTempSubdirectory("wppack-").FullName;

        // Copy directories: types, i18n, settings, docs
        foreach (var sub in new[] { "types", "i18n", "settings", "docs" })
        {
            var srcSub = Path.Combine(sourcePackageDir, sub);
            if (Directory.Exists(srcSub))
            {
                var dstSub = Path.Combine(tempDir, sub);
                Directory.CreateDirectory(dstSub);
                foreach (var file in Directory.EnumerateFiles(srcSub, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(srcSub, file);
                    var dstFile = Path.Combine(dstSub, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
                    File.Copy(file, dstFile, true);
                }
            }
        }

        // Copy assembly to lib/
        var libDir = Path.Combine(tempDir, "lib");
        Directory.CreateDirectory(libDir);
        var dstAssembly = Path.Combine(libDir, Path.GetFileName(assemblyLocation));
        File.Copy(assemblyLocation, dstAssembly, true);

        // Read source manifest
        var manifestPath = Path.Combine(sourcePackageDir, "manifest.json");
        var manifestText = File.ReadAllText(manifestPath);
        var manifestNode = JsonNode.Parse(manifestText)!.AsObject();

        // Build files array
        var filesArray = new JsonArray();
        foreach (var file in Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(tempDir, file).Replace('\\', '/');
            if (rel == "manifest.json" || rel == "signature.sig")
            {
                continue;
            }

            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
            var fileObj = new JsonObject
            {
                ["path"] = rel,
                ["sha256"] = hash,
            };
            filesArray.Add(fileObj);
        }

        manifestNode["files"] = filesArray;

        var dstManifestPath = Path.Combine(tempDir, "manifest.json");
        File.WriteAllText(dstManifestPath, manifestNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return tempDir;
    }
}
