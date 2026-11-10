using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Watchpoint.Integrations.Tests.Support;

/// <summary>Builds a test package (folder or <c>.wpkg</c>): manifest with file hashes, one server type, translations, signature.</summary>
public sealed class TestPackage
{
    private static readonly string[] _platforms = ["win-x64", "linux-x64", "linux-arm64"];
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly JsonArray _connectors = [];
    private readonly List<Action<JsonObject>> _manifestChanges = [];
    private readonly List<Action<Dictionary<string, byte[]>>> _afterSigning = [];

    public TestPackage(string id = "vendor.acme", string version = "1.0.0")
    {
        Id = id;
        Version = version;
        ServerType = id + ".server";
        _files[$"types/{ServerType}.json"] = Encoding.UTF8.GetBytes(TypeDocument(ServerType));
        foreach (var culture in new[] { "en", "fr", "ar" })
        {
            _files[$"i18n/{culture}.json"] = Encoding.UTF8.GetBytes(Bundle(id, ServerType));
        }
    }

    public string Id { get; }

    public string Version { get; }

    public string ServerType { get; }

    public string AssemblyName => "Vendor." + string.Concat(Id.Split('.').Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    public string ConnectorClass => $"{AssemblyName}::{AssemblyName}.TestConnector";

    /// <summary>Adds a valid connector (compiled class <c>TestConnector</c>) for <see cref="ServerType"/>.</summary>
    public TestPackage WithConnector(string members = "", JsonNode? settingsSchema = null, params (string Name, byte[] Image)[] privateDependencies)
    {
        var source = TestAssemblies.ConnectorSource(AssemblyName, "TestConnector", ServerType, members: members);
        var image = TestAssemblies.Compile(AssemblyName, source, references: [.. privateDependencies.Select(d => d.Image)]);
        foreach (var (name, dependency) in privateDependencies)
        {
            _files[$"lib/{name}.dll"] = dependency;
        }

        return WithConnectorAssembly(image, ConnectorClass, settingsSchema);
    }

    /// <summary>Adds a connector entry for an assembly compiled by the test.</summary>
    public TestPackage WithConnectorAssembly(byte[] image, string connectorClass, JsonNode? settingsSchema = null, string? assemblyFile = null)
    {
        _files[$"lib/{assemblyFile ?? connectorClass.Split("::")[0]}.dll"] = image;
        _connectors.Add(new JsonObject
        {
            ["class"] = connectorClass,
            ["serverType"] = ServerType,
            ["supportedVendorVersions"] = "*",
            ["features"] = new JsonArray("discovery"),
            ["settingsSchema"] = settingsSchema ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            ["secretsSchema"] = new JsonObject
            {
                ["fields"] = new JsonArray(new JsonObject { ["id"] = "password", ["kind"] = "password", ["required"] = true }),
            },
        });
        return this;
    }

    public TestPackage WithFile(string path, byte[] content)
    {
        _files[path] = content;
        return this;
    }

    public TestPackage WithFile(string path, string content) => WithFile(path, Encoding.UTF8.GetBytes(content));

    /// <summary>Changes the manifest before it is hashed and signed.</summary>
    public TestPackage WithManifest(Action<JsonObject> change)
    {
        _manifestChanges.Add(change);
        return this;
    }

    /// <summary>Changes the package files after the manifest was written and signed (tampering).</summary>
    public TestPackage AfterSigning(Action<Dictionary<string, byte[]>> change)
    {
        _afterSigning.Add(change);
        return this;
    }

    /// <summary>The files of the package, manifest and signature included.</summary>
    public Dictionary<string, byte[]> Build(TestKey? key)
    {
        var manifest = new JsonObject
        {
            ["$schema"] = "https://schemas.watchpoint.dev/manifest/1.json",
            ["id"] = Id,
            ["version"] = Version,
            ["vendor"] = "Acme",
            ["category"] = "generic",
            ["contract"] = "^1.0",
            ["minWatchpointVersion"] = "1.0.0",
            ["platforms"] = new JsonArray([.. _platforms.Select(r => (JsonNode)r)]),
            ["nativeCode"] = false,
            ["isolation"] = "inProcessAllowed",
            ["licence"] = new JsonObject { ["class"] = "included" },
            ["connectors"] = _connectors.DeepClone(),
            ["types"] = new JsonArray($"types/{ServerType}.json"),
            ["capabilities"] = new JsonArray(),
            ["ruleTemplates"] = new JsonArray(),
            ["i18n"] = "i18n/",
            ["files"] = new JsonArray([.. _files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => (JsonNode)new JsonObject
            {
                ["path"] = f.Key,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(f.Value)),
            })]),
        };
        foreach (var change in _manifestChanges)
        {
            change(manifest);
        }

        var files = new Dictionary<string, byte[]>(_files, StringComparer.Ordinal);
        var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        files["manifest.json"] = manifestBytes;
        if (key is not null)
        {
            files["signature.sig"] = Encoding.UTF8.GetBytes(key.SignatureFile(manifestBytes));
        }

        foreach (var change in _afterSigning)
        {
            change(files);
        }

        return files;
    }

    /// <summary>Writes the package as an unpacked folder.</summary>
    public string WriteFolder(string directory, TestKey? key)
    {
        foreach (var (path, content) in Build(key))
        {
            var file = Path.Combine(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, content);
        }

        return directory;
    }

    /// <summary>Writes the package as a <c>.wpkg</c> file.</summary>
    public string WriteWpkg(string file, TestKey? key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, ToWpkg(key));
        return file;
    }

    /// <summary>The <c>.wpkg</c> bytes.</summary>
    public byte[] ToWpkg(TestKey? key)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in Build(key).OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                using var entry = zip.CreateEntry(path, CompressionLevel.Fastest).Open();
                entry.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static string TypeDocument(string id) => $$"""
        {
          "$schema": "https://schemas.watchpoint.dev/device-type/1.json",
          "id": "{{id}}",
          "version": "1.0.0",
          "role": "server",
          "category": "generic",
          "extends": { "id": "generic.server", "version": "^1.0" },
          "capabilities": [ { "id": "health", "version": "^1.0" } ],
          "licenceClass": "free",
          "display": { "icon": "server", "symbol": "generic.server", "primaryProperty": "health.connectivity" }
        }
        """;

    private static string Bundle(string packageId, string typeId)
    {
        JsonObject Nest(string dotted, JsonObject leaf)
        {
            var node = leaf;
            foreach (var part in dotted.Split('.').Reverse())
            {
                node = new JsonObject { [part] = node };
            }

            return node;
        }

        var root = new JsonObject
        {
            ["packages"] = Nest(packageId, new JsonObject { ["name"] = "Acme test package" }),
            ["types"] = Nest(typeId, new JsonObject { ["name"] = "Acme server", ["description"] = "Test server" }),
        };
        return root.ToJsonString();
    }
}
