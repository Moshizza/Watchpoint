using System.Security.Cryptography;
using System.Text.Json;
using Watchpoint.Integration.Abstractions;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Documents;
using Watchpoint.Model.Documents.Library;

namespace Watchpoint.Integrations.Internal.Packages;

/// <summary>Outcome of the verification of an unpacked package.</summary>
/// <param name="Manifest">The manifest, when it could be read.</param>
/// <param name="ManifestSha256">SHA-256 of <c>manifest.json</c> (identifies the content: the manifest lists every file hash).</param>
/// <param name="Problems">Errors and warnings; the package is invalid when one is an error.</param>
/// <param name="SignedByDevelopmentKey">Whether the signing key is a development key.</param>
/// <param name="HasLocator">Per server type: whether the connector implements <c>IServerLocator</c>.</param>
/// <param name="Schemas">Per server type: the settings schema.</param>
/// <param name="CheckContext">Weak reference to the code-check load context, when the check ran.</param>
internal sealed record PackageVerification(
    ManifestDocument? Manifest,
    string? ManifestSha256,
    IReadOnlyList<DocumentProblem> Problems,
    bool SignedByDevelopmentKey,
    IReadOnlyDictionary<string, bool> HasLocator,
    IReadOnlyDictionary<string, ConnectionSettingsSchema> Schemas,
    WeakReference? CheckContext)
{
    public bool IsValid => Manifest is not null && Problems.All(p => p.Severity != ProblemSeverity.Error);
}

/// <summary>
/// Verification steps 2–7 of spec IRT §4.3 on an unpacked package: manifest, signature, file hashes, compatibility,
/// library validation, code check. Steps run in order; the first failing step ends the verification.
/// </summary>
internal sealed class PackageVerifier(IDocumentReader reader, ILibraryValidator validator, SignatureVerifier signatures, IntegrationsOptions options)
{
    private static readonly IReadOnlyDictionary<string, bool> _noLocators = new Dictionary<string, bool>();
    private static readonly IReadOnlyDictionary<string, ConnectionSettingsSchema> _noSchemas = new Dictionary<string, ConnectionSettingsSchema>();

    /// <summary>Verifies the package unpacked in <paramref name="packageDir"/>.</summary>
    /// <param name="packageDir">The staging folder.</param>
    /// <param name="dependencies">The standard library and the other active packages.</param>
    public PackageVerification Verify(string packageDir, IReadOnlyList<LibraryFolder> dependencies)
    {
        var problems = new List<DocumentProblem>();

        // 2. Manifest and documents.
        var manifestPath = Path.Combine(packageDir, PackageProblems.ManifestFile);
        if (!File.Exists(manifestPath))
        {
            problems.Add(PackageProblems.Error(ProblemCodes.ManifestFileMissing, PackageProblems.ManifestFile, ("file", PackageProblems.ManifestFile)));
            return Failed(null, null, problems);
        }

        if (new FileInfo(manifestPath).Length > LibraryLoader.MaxDocumentBytes)
        {
            problems.Add(PackageProblems.Error(Kernel.Results.CommonErrorCodes.ValueTooLong.Value, PackageProblems.ManifestFile, ("max", LibraryLoader.MaxDocumentBytes)));
            return Failed(null, null, problems);
        }

        var manifestBytes = File.ReadAllBytes(manifestPath);
        var manifestSha = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
        var read = reader.ReadManifest(manifestBytes, PackageProblems.ManifestFile);
        if (!read.TryGetValue(out var manifest, out var manifestError))
        {
            problems.AddRange(PackageProblems.FromError(manifestError, PackageProblems.ManifestFile));
            return Failed(null, manifestSha, problems);
        }

        var folder = LibraryLoader.Load(packageDir, reader);
        if (folder.LoadProblems.Count > 0)
        {
            problems.AddRange(folder.LoadProblems);
            return Failed(manifest, manifestSha, problems);
        }

        // 3. Signature.
        var signature = signatures.Verify(packageDir, manifestBytes);
        if (!signature.TryGetValue(out var developmentKey, out var signatureError))
        {
            problems.AddRange(PackageProblems.FromError(signatureError, SignatureVerifier.SignatureFile));
            return Failed(manifest, manifestSha, problems);
        }

        // 4. File hashes.
        CheckFiles(packageDir, manifest, problems);
        if (HasErrors(problems))
        {
            return Failed(manifest, manifestSha, problems, developmentKey);
        }

        // 5. Compatibility.
        if (!ContractVersion.IsSupported(manifest.Contract.Raw))
        {
            problems.Add(PackageProblems.Error(IntegrationErrors.PackageContractUnsupported, PackageProblems.ManifestFile,
                ("required", manifest.Contract.Raw), ("runtime", ContractVersion.Current)));
        }

        if (manifest.MinWatchpointVersion > options.ProductVersion)
        {
            problems.Add(PackageProblems.Error(IntegrationErrors.PackageProductTooOld, PackageProblems.ManifestFile,
                ("required", manifest.MinWatchpointVersion.ToString()), ("product", options.ProductVersion.ToString())));
        }

        if (!manifest.Platforms.Intersect(SharedAssemblies.CurrentRuntimeIdentifiers, StringComparer.Ordinal).Any())
        {
            problems.Add(PackageProblems.Error(IntegrationErrors.PackagePlatformUnsupported, PackageProblems.ManifestFile,
                ("platform", SharedAssemblies.CurrentRuntimeIdentifiers[^1])));
        }

        if (HasErrors(problems))
        {
            return Failed(manifest, manifestSha, problems, developmentKey);
        }

        // 6. Library validation, with the standard library and the other active packages.
        problems.AddRange(validator.Validate(new LibrarySet(folder, dependencies), new LibraryValidationOptions()).Problems);
        var schemas = ReadSchemas(packageDir, manifest, problems);
        if (HasErrors(problems))
        {
            return Failed(manifest, manifestSha, problems, developmentKey);
        }

        // 7. Code check.
        var code = ConnectorCodeChecker.Check(packageDir, manifest);
        problems.AddRange(code.Problems);
        return new PackageVerification(manifest, manifestSha, problems, developmentKey, code.HasLocator, schemas, code.Context);
    }

    private static PackageVerification Failed(ManifestDocument? manifest, string? sha, List<DocumentProblem> problems, bool developmentKey = false) =>
        new(manifest, sha, problems, developmentKey, _noLocators, _noSchemas, null);

    private static bool HasErrors(List<DocumentProblem> problems) => problems.Any(p => p.Severity == ProblemSeverity.Error);

    private static void CheckFiles(string packageDir, ManifestDocument manifest, List<DocumentProblem> problems)
    {
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            listed[file.Path] = file.Sha256;
        }

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(packageDir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = LibraryLoader.Relative(packageDir, path);
            if (relative is PackageProblems.ManifestFile or SignatureVerifier.SignatureFile)
            {
                continue;
            }

            present.Add(relative);
            if (!listed.TryGetValue(relative, out var expected))
            {
                problems.Add(PackageProblems.Error(IntegrationErrors.PackageFileUnlisted, relative, ("file", relative)));
                continue;
            }

            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(PackageProblems.Error(IntegrationErrors.PackageHashMismatch, relative, ("file", relative), ("actual", actual)));
            }
        }

        foreach (var missing in listed.Keys.Where(f => !present.Contains(f)).Order(StringComparer.Ordinal))
        {
            problems.Add(PackageProblems.Error(IntegrationErrors.PackageHashMismatch, missing, ("file", missing), ("actual", null)));
        }
    }

    private static Dictionary<string, ConnectionSettingsSchema> ReadSchemas(string packageDir, ManifestDocument manifest, List<DocumentProblem> problems)
    {
        var schemas = new Dictionary<string, ConnectionSettingsSchema>(StringComparer.Ordinal);
        for (var i = 0; i < manifest.Connectors.Count; i++)
        {
            var connector = manifest.Connectors[i];
            JsonElement? settings = connector.InlineSettingsSchema?.RawElement;
            if (settings is null && connector.SettingsSchemaPath is { } relative)
            {
                settings = ReadSchemaFile(packageDir, relative, i, problems);
            }

            if (settings is not { } schema)
            {
                continue;
            }

            JsonElement? hints = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("x-ui", out var ui) ? ui.Clone() : null;
            schemas[connector.ServerType] = new ConnectionSettingsSchema(connector.ServerType, schema.Clone(),
                [.. (connector.SecretsSchema?.Fields ?? []).Select(f => new SecretFieldSchema(f.Id, f.Kind, f.Required))], hints);
        }

        return schemas;
    }

    private static JsonElement? ReadSchemaFile(string packageDir, string relative, int index, List<DocumentProblem> problems)
    {
        DocumentProblem Invalid(string reason) => new(ProblemSeverity.Error, PackageProblems.ManifestFile, $"/connectors/{index}/settingsSchema",
            ErrorCode.FromTrusted(ProblemCodes.DocumentInvalid), new Dictionary<string, object?> { ["reason"] = reason, ["file"] = relative });

        var path = Path.GetFullPath(Path.Combine(packageDir, relative));
        if (!PackageUnpacker.IsSafeRelativePath(relative) || !path.StartsWith(packageDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
        {
            problems.Add(Invalid("settings schema file not found in the package"));
            return null;
        }

        if (new FileInfo(path).Length > LibraryLoader.MaxDocumentBytes)
        {
            problems.Add(Invalid("settings schema too large"));
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var parsed = ParameterSchema.Parse(document.RootElement.Clone());
            if (parsed.TryGetValue(out var schema, out var error))
            {
                return schema.RawElement;
            }

            problems.AddRange(PackageProblems.FromError(error, relative));
            return null;
        }
        catch (JsonException ex)
        {
            problems.Add(Invalid(ex.Message));
            return null;
        }
    }

}
