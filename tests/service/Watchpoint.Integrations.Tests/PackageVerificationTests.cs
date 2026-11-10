using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Internal.Packages;
using Watchpoint.Integrations.Tests.Support;
using Watchpoint.Model.Documents.Library;

namespace Watchpoint.Integrations.Tests;

/// <summary>Verification steps of spec IRT §4.3–§4.4 (unpack, signature, hashes, compatibility, library, code check).</summary>
[Trait("layer", "U")]
[Trait("spec", "IRT")]
public sealed class PackageVerificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IRT_U_003_Unpack_rejects_dot_dot_path_entries()
    {
        await using var h = new RuntimeHarness();

        // Through the catalogue: the package is invalid and nothing was written outside the staging folder.
        File.WriteAllBytes(h.UserFile("evil.wpkg"), Zip(("manifest.json", "{}"), ("../../escaped.txt", "x")));
        await h.Catalog.ScanAsync(Ct);

        var package = h.Catalog.List().ShouldHaveSingleItem();
        package.State.ShouldBe(PackageState.Invalid);
        var problem = package.Problems.ShouldHaveSingleItem();
        problem.Code.Value.ShouldBe(IntegrationErrors.PackageUnsafeArchive);
        problem.Args["reason"].ShouldBe("path");
        Directory.EnumerateFiles(h.Root, "escaped.txt", SearchOption.AllDirectories).ShouldBeEmpty();
        File.Exists(Path.Combine(Path.GetDirectoryName(h.Root)!, "escaped.txt")).ShouldBeFalse();
        Directory.GetFileSystemEntries(Path.Combine(h.Options.CachePath, ".staging")).ShouldBeEmpty("a refused archive leaves no staging folder");

        // Every way of leaving the target folder (zip-slip) or naming an absolute path.
        foreach (var name in new[] { "../evil", "a/../../evil", "a/..", "/etc/evil", "C:/evil", "C:evil", "..\\evil", "a\\..\\..\\evil", "a//b", "a/./b", "evil. ", "evil\u0001" })
        {
            var target = Path.Combine(h.Root, "unpack-" + Guid.NewGuid().ToString("N"));
            using var archive = new MemoryStream(Zip(("ok.txt", "ok"), (name, "x")));
            var result = PackageUnpacker.Unpack(archive, target, UnpackLimits.Default);
            result.IsSuccess.ShouldBeFalse(name);
            result.Error.Code.Value.ShouldBe(IntegrationErrors.PackageUnsafeArchive, name);
            Directory.Exists(target).ShouldBeFalse($"{name}: nothing is left of a refused archive");
        }
    }

    [Fact]
    public async Task IRT_U_004_Unpack_rejects_symlinks_too_many_files_too_large_and_zip_bombs()
    {
        await using var h = new RuntimeHarness();
        string Reason(byte[] archive, UnpackLimits limits)
        {
            var target = Path.Combine(h.Root, "unpack-" + Guid.NewGuid().ToString("N"));
            using var stream = new MemoryStream(archive);
            var result = PackageUnpacker.Unpack(stream, target, limits);
            result.IsSuccess.ShouldBeFalse();
            result.Error.Code.Value.ShouldBe(IntegrationErrors.PackageUnsafeArchive);
            Directory.Exists(target).ShouldBeFalse();
            return (string)result.Error.Args["reason"]!;
        }

        // Symbolic link (Unix mode in the external attributes).
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("lib/link");
                entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                using var content = entry.Open();
                content.Write("/etc/passwd"u8);
            }

            Reason(stream.ToArray(), UnpackLimits.Default).ShouldBe("symlink");
        }

        // More than 10,000 files (default limits).
        Reason(Zip([.. Enumerable.Range(0, 10_001).Select(i => ($"f/{i}.txt", string.Empty))]), UnpackLimits.Default).ShouldBe("tooManyFiles");

        // Larger than the limit (incompressible content, so the ratio is not what refuses it).
        var random = new byte[128 * 1024];
        new Random(15).NextBytes(random);
        Reason(Zip(CompressionLevel.NoCompression, ("big.bin", random)), new UnpackLimits(MaxTotalBytes: 64 * 1024)).ShouldBe("tooLarge");

        // Zip bomb: 8 MB of zeros compress to a few kilobytes (ratio far above 100).
        Reason(Zip(CompressionLevel.Optimal, ("bomb.bin", new byte[8 * 1024 * 1024])), UnpackLimits.Default).ShouldBe("compressionRatio");

        // An unpacked folder with a symbolic link is refused the same way (not on Windows: needs a privilege there).
        if (!OperatingSystem.IsWindows())
        {
            var folder = new TestPackage().WithConnector().WriteFolder(h.UserFile("vendor.acme"), h.ReleaseKey);
            File.CreateSymbolicLink(Path.Combine(folder, "lib", "link.dll"), "/etc/passwd");
            await h.Catalog.ScanAsync(Ct);
            var package = h.Catalog.List().ShouldHaveSingleItem();
            package.State.ShouldBe(PackageState.Invalid);
            package.Problems.ShouldHaveSingleItem().Args["reason"].ShouldBe("symlink");
        }
    }

    [Fact]
    public async Task IRT_U_005_Valid_signature_accepted()
    {
        await using var h = new RuntimeHarness(development: false);

        var package = await h.InstallAsync(new TestPackage().WithConnector());

        package.Problems.ShouldBeEmpty();
        package.State.ShouldBe(PackageState.Active, "verified packages are activated automatically");
        package.SignedByDevelopmentKey.ShouldBeFalse();
    }

    [Fact]
    public async Task IRT_U_006_Unsigned_package_invalid_even_in_Development()
    {
        await using var h = new RuntimeHarness(development: true);

        var package = await h.InstallAsync(new TestPackage().WithConnector(), unsigned: true);

        package.State.ShouldBe(PackageState.Invalid);
        RuntimeHarness.ErrorCodes(package).ShouldBe([IntegrationErrors.PackageUnsigned]);
        h.Types.IsRegistered(package.Id, package.Version).ShouldBeFalse();
    }

    [Fact]
    public async Task IRT_U_007_Tampered_manifest()
    {
        await using var h = new RuntimeHarness();
        var tampered = new TestPackage().WithConnector().AfterSigning(files =>
            files["manifest.json"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files["manifest.json"]).Replace("\"Acme\"", "\"Acmf\"", StringComparison.Ordinal)));

        var package = await h.InstallAsync(tampered);

        package.State.ShouldBe(PackageState.Invalid);
        RuntimeHarness.ErrorCodes(package).ShouldBe([IntegrationErrors.PackageSignatureInvalid]);
    }

    [Fact]
    public async Task IRT_U_008_Unknown_key()
    {
        await using var h = new RuntimeHarness();
        using var unknown = new TestKey("someone-else");

        var package = await h.InstallAsync(new TestPackage().WithConnector(), unknown);

        package.State.ShouldBe(PackageState.Invalid);
        var problem = package.Problems.ShouldHaveSingleItem();
        problem.Code.Value.ShouldBe(IntegrationErrors.PackageUntrustedKey);
        problem.Args["keyId"].ShouldBe("someone-else");
    }

    [Fact]
    public async Task IRT_U_009_Dev_key_only_in_Development()
    {
        using var devKey = new TestKey("dev-alice");

        await using (var production = new RuntimeHarness(development: false))
        {
            devKey.WriteDevelopmentKey(production.Options.DevKeysPath);
            var package = await production.InstallAsync(new TestPackage().WithConnector(), devKey);
            package.State.ShouldBe(PackageState.Invalid);
            RuntimeHarness.ErrorCodes(package).ShouldBe([IntegrationErrors.PackageUntrustedKey]);
        }

        await using (var development = new RuntimeHarness(development: true))
        {
            devKey.WriteDevelopmentKey(development.Options.DevKeysPath);
            var package = await development.InstallAsync(new TestPackage().WithConnector(), devKey);
            package.State.ShouldBe(PackageState.Active);
            package.SignedByDevelopmentKey.ShouldBeTrue();
            development.Logs.WithEventId(1804).ShouldHaveSingleItem();
            development.Catalog.List().ShouldNotContain(p => p.Id.Value == "unknown.package", "dev-keys is not scanned for packages");
        }
    }

    [Fact]
    public async Task IRT_U_010_Hash_mismatch_and_unlisted_file()
    {
        await using var h = new RuntimeHarness();

        var changed = await h.InstallAsync(new TestPackage("vendor.changed").WithConnector().WithFile("docs/en.md", "original")
            .AfterSigning(files => files["docs/en.md"] = "changed"u8.ToArray()));
        changed.State.ShouldBe(PackageState.Invalid);
        var mismatch = changed.Problems.ShouldHaveSingleItem();
        mismatch.Code.Value.ShouldBe(IntegrationErrors.PackageHashMismatch);
        mismatch.Args["file"].ShouldBe("docs/en.md");

        var extra = await h.InstallAsync(new TestPackage("vendor.extra").WithConnector()
            .AfterSigning(files => files["lib/Injected.dll"] = [1, 2, 3]));
        extra.State.ShouldBe(PackageState.Invalid);
        var unlisted = extra.Problems.ShouldHaveSingleItem();
        unlisted.Code.Value.ShouldBe(IntegrationErrors.PackageFileUnlisted);
        unlisted.Args["file"].ShouldBe("lib/Injected.dll");
    }

    [Fact]
    public async Task IRT_U_011_Contract_2_0_unsupported()
    {
        await using var h = new RuntimeHarness();

        var package = await h.InstallAsync(new TestPackage().WithConnector().WithManifest(m => m["contract"] = "^2.0"));

        package.State.ShouldBe(PackageState.Invalid);
        RuntimeHarness.ErrorCodes(package).ShouldBe([IntegrationErrors.PackageContractUnsupported]);
    }

    [Fact]
    public async Task IRT_U_012_Platform_unsupported()
    {
        await using var h = new RuntimeHarness();
        var other = SharedAssemblies.CurrentRuntimeIdentifiers.Contains("win-x86") ? "linux-arm64" : "win-x86";

        var package = await h.InstallAsync(new TestPackage().WithConnector().WithManifest(m => m["platforms"] = new JsonArray(other)));

        package.State.ShouldBe(PackageState.Invalid);
        RuntimeHarness.ErrorCodes(package).ShouldBe([IntegrationErrors.PackagePlatformUnsupported]);
    }

    [Fact]
    public async Task IRT_U_013_Product_too_old()
    {
        await using var h = new RuntimeHarness();

        var package = await h.InstallAsync(new TestPackage().WithConnector().WithManifest(m => m["minWatchpointVersion"] = "99.0.0"));

        package.State.ShouldBe(PackageState.Invalid);
        var problem = package.Problems.ShouldHaveSingleItem();
        problem.Code.Value.ShouldBe(IntegrationErrors.PackageProductTooOld);
        problem.Args["product"].ShouldBe("1.2.0");
    }

    [Fact]
    public async Task IRT_U_014_Library_validation_errors_surface_as_problems()
    {
        await using var h = new RuntimeHarness();
        var broken = new TestPackage().WithConnector().WithFile("types/vendor.acme.server.json", """
            {
              "$schema": "https://schemas.watchpoint.dev/device-type/1.json",
              "id": "vendor.acme.server", "version": "1.0.0", "role": "server", "category": "generic",
              "extends": { "id": "generic.server", "version": "^1.0" },
              "capabilities": [ { "id": "health", "version": "^1.0" }, { "id": "vendor.acmeMissing", "version": "^1.0" } ],
              "licenceClass": "free",
              "display": { "icon": "server", "symbol": "generic.server", "primaryProperty": "health.connectivity" }
            }
            """);

        var package = await h.InstallAsync(broken);

        package.State.ShouldBe(PackageState.Invalid);
        RuntimeHarness.ErrorCodes(package).ShouldContain(ProblemCodes.CapabilityMissing, string.Join("; ", package.Problems));
        package.Problems.ShouldAllBe(p => p.Code.Value.StartsWith("scm.", StringComparison.Ordinal));
        h.Types.IsRegistered(package.Id, package.Version).ShouldBeFalse();
    }

    [Fact]
    public async Task IRT_U_015_Connector_class_checks()
    {
        await using var h = new RuntimeHarness();
        async Task<(string Reason, string Class)> ReasonAsync(string id, string className, string? serverType, string modifiers = "public sealed", bool implement = true, string? manifestClass = null)
        {
            var package = new TestPackage(id);
            var image = TestAssemblies.Compile(package.AssemblyName, TestAssemblies.ConnectorSource(package.AssemblyName, className, serverType, modifiers, implement: implement));
            var info = await h.InstallAsync(package.WithConnectorAssembly(image, manifestClass ?? $"{package.AssemblyName}::{package.AssemblyName}.{className}", assemblyFile: package.AssemblyName));
            info.State.ShouldBe(PackageState.Invalid);
            var problem = info.Problems.Where(p => p.Severity == ProblemSeverity.Error).ShouldHaveSingleItem();
            problem.Code.Value.ShouldBe(IntegrationErrors.PackageConnectorInvalid);
            h.Types.IsRegistered(info.Id, info.Version).ShouldBeFalse();
            return ((string)problem.Args["reason"]!, (string)problem.Args["class"]!);
        }

        (await ReasonAsync("vendor.missing", "Real", "vendor.missing.server", manifestClass: "Vendor.Missing::Vendor.Missing.Ghost"))
            .ShouldBe(("classMissing", "Vendor.Missing::Vendor.Missing.Ghost"));
        (await ReasonAsync("vendor.abstract", "Base", "vendor.abstract.server", "public abstract")).Reason.ShouldBe("abstract");
        (await ReasonAsync("vendor.hidden", "Hidden", "vendor.hidden.server", "internal sealed")).Reason.ShouldBe("notPublic");
        (await ReasonAsync("vendor.plain", "Plain", "vendor.plain.server", implement: false)).Reason.ShouldBe("notConnector");
        (await ReasonAsync("vendor.noattribute", "Bare", null)).Reason.ShouldBe("attributeMissing");
        (await ReasonAsync("vendor.mismatch", "Other", "vendor.mismatch.other")).Reason.ShouldBe("attributeMismatch");
    }

    [Fact]
    public async Task IRT_U_016_Code_check_context_unloaded()
    {
        await using var h = new RuntimeHarness();
        var folder = new TestPackage().WithConnector().WriteFolder(Path.Combine(h.Root, "staged"), h.ReleaseKey);

        var verification = h.Verifier.Verify(folder, [new RepositoryStandardLibrary().Folder]);

        verification.IsValid.ShouldBeTrue(string.Join("; ", verification.Problems));
        verification.CheckContext.ShouldNotBeNull();
        LoadContextTests.IsCollected(verification.CheckContext).ShouldBeTrue("the throw-away check context is unloaded and nothing keeps it alive");
        AppDomain.CurrentDomain.GetAssemblies().ShouldNotContain(a => a.GetName().Name == "Vendor.Acme" && !a.IsCollectible);
    }

    private static byte[] Zip(params (string Name, string Content)[] entries) =>
        Zip(CompressionLevel.Fastest, [.. entries.Select(e => (e.Name, Encoding.UTF8.GetBytes(e.Content)))]);

    private static byte[] Zip(CompressionLevel level, params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entry = zip.CreateEntry(name, level).Open();
                entry.Write(content);
            }
        }

        return stream.ToArray();
    }
}
