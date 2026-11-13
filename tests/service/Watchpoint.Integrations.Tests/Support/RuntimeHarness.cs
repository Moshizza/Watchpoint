using Microsoft.Extensions.Logging;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Internal.Packages;
using Watchpoint.Integrations.Testing;
using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Documents;
using Watchpoint.Model.Documents.Library;
using Watchpoint.Testing.Logging;
using Watchpoint.Testing.Time;

namespace Watchpoint.Integrations.Tests.Support;

/// <summary>A package catalogue on temporary folders with fakes for MOD, the store and the connections.</summary>
internal sealed class RuntimeHarness : IAsyncDisposable
{
    private readonly LoggerFactory _loggers;
    private readonly List<IAsyncDisposable> _owned = [];

    public RuntimeHarness(bool development = false, UnpackLimits? limits = null, bool trustReleaseKey = true)
    {
        Root = Directory.CreateTempSubdirectory("wp-irt-").FullName;
        Options = new IntegrationsOptions
        {
            BuiltinPath = Directory.CreateDirectory(Path.Combine(Root, "builtin")).FullName,
            PackagesPath = Directory.CreateDirectory(Path.Combine(Root, "integrations")).FullName,
            CachePath = Path.Combine(Root, "integrations", ".cache"),
            DevKeysPath = Path.Combine(Root, "integrations", "dev-keys"),
            IsDevelopment = development,
            ProductVersion = new SemVersion(1, 2, 0),
        };
        _loggers = new LoggerFactory([Logs]);
        var keys = new TrustedKeys(Options, Clock, _loggers.CreateLogger<TrustedKeys>(),
            trustReleaseKey ? [new TrustedKey(ReleaseKey.KeyId, ReleaseKey.PublicKey, Development: false)] : []);
        Verifier = new PackageVerifier(DocumentReaderFactory.FromEmbeddedSchemas(), new LibraryValidator(), new SignatureVerifier(keys), Options);
        Pool = new PackageLoadContextPool(Clock, _loggers.CreateLogger<PackageLoadContextPool>());
        Catalog = new PackageCatalog(Options, Verifier, Types, new RepositoryStandardLibrary(), DocumentReaderFactory.FromEmbeddedSchemas(),
            Store, Connections, Pool, Clock, _loggers.CreateLogger<PackageCatalog>(), limits);
    }

    public string Root { get; }

    public IntegrationsOptions Options { get; }

    public TestClock Clock { get; } = new();

    public LogCollector Logs { get; } = new();

    public FakeTypeCatalog Types { get; } = new();

    public FakeConnections Connections { get; } = new();

    public InMemoryPackageStore Store { get; } = new();

    public PackageVerifier Verifier { get; }

    public PackageLoadContextPool Pool { get; }

    public PackageCatalog Catalog { get; }

    /// <summary>The release key the harness trusts.</summary>
    public TestKey ReleaseKey { get; } = new("wp-test-release");

    public static UserId Actor { get; } = UserId.New();

    public string UserFile(string name) => Path.Combine(Options.PackagesPath, name);

    public string BuiltinFile(string name) => Path.Combine(Options.BuiltinPath, name);

    public PackageWatcher Watcher(IFolderChangeSourceFactory? sources = null)
    {
        var watcher = new PackageWatcher(Catalog, Options, sources ?? new FileSystemFolderChangeSource.Factory(), Clock, _loggers.CreateLogger<PackageWatcher>());
        _owned.Add(watcher);
        return watcher;
    }

    /// <summary>Installs a package signed with the release key and returns its info.</summary>
    public async Task<PackageInfo> InstallAsync(TestPackage package, TestKey? key = null, bool unsigned = false)
    {
        using var stream = new MemoryStream(package.ToWpkg(unsigned ? null : key ?? ReleaseKey));
        var result = await Catalog.InstallAsync(stream, $"{package.Id}-{package.Version}.wpkg", Actor, TestContext.Current.CancellationToken);
        return result.Value;
    }

    public static IEnumerable<string> ErrorCodes(PackageInfo package) =>
        package.Problems.Where(p => p.Severity == ProblemSeverity.Error).Select(p => p.Code.Value);

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in _owned)
        {
            await owned.DisposeAsync();
        }

        Catalog.Dispose();
        ReleaseKey.Dispose();
        _loggers.Dispose();
        Logs.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows keeps loaded assemblies locked until their load context is collected.
        }
    }
}
