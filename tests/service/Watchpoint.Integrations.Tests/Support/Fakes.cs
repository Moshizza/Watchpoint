using Watchpoint.Integrations.Internal.Packages;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Model.Documents.Library;

namespace Watchpoint.Integrations.Tests.Support;

/// <summary>Records what the runtime registers with MOD; can be told to refuse a package.</summary>
public sealed class FakeTypeCatalog : ITypeCatalog
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PackageTypeDocuments> _registered = new(StringComparer.Ordinal);

    public Func<PackageId, SemVersion, Error?> Refuse { get; set; } = (_, _) => null;

    public long Revision { get; private set; }

    public event EventHandler<CatalogChangedEventArgs>? Changed;

    public bool IsRegistered(PackageId id, SemVersion version)
    {
        lock (_lock)
        {
            return _registered.ContainsKey($"{id.Value}@{version}");
        }
    }

    public PackageTypeDocuments? Documents(string id, string version)
    {
        lock (_lock)
        {
            return _registered.GetValueOrDefault($"{id}@{version}");
        }
    }

    public Task<Result<CatalogChange>> RegisterPackageAsync(PackageId package, SemVersion packageVersion, PackageTypeDocuments documents, CancellationToken ct)
    {
        if (Refuse(package, packageVersion) is { } error)
        {
            return Task.FromResult<Result<CatalogChange>>(error);
        }

        CatalogChange change;
        lock (_lock)
        {
            _registered[$"{package.Value}@{packageVersion}"] = documents;
            Revision++;
            change = new CatalogChange([.. documents.Types.Select(t => new TypeRef(t.Id, t.Version))], []);
        }

        Changed?.Invoke(this, new CatalogChangedEventArgs(change, Revision));
        return Task.FromResult<Result<CatalogChange>>(change);
    }

    public Task<Result> UnregisterPackageAsync(PackageId package, SemVersion packageVersion, CancellationToken ct)
    {
        lock (_lock)
        {
            _registered.Remove($"{package.Value}@{packageVersion}");
        }

        return Task.FromResult(Result.Success());
    }

    public Result<DeviceTypeDefinition> Get(TypeRef type) => throw new NotSupportedException();

    public Result<DeviceTypeDefinition> GetLatest(TypeId id, int major) => throw new NotSupportedException();

    public IReadOnlyList<DeviceTypeDefinition> List(TypeQuery query) => throw new NotSupportedException();

    public Result<CapabilityDefinition> GetCapability(CapabilityId id, SemVersion version) => throw new NotSupportedException();
}

/// <summary>The standard library of the repository (<c>types/</c>), read from disk once.</summary>
public sealed class RepositoryStandardLibrary : IStandardLibrary
{
    private static readonly Lazy<LibraryFolder> _folder = new(() =>
        LibraryLoader.Load(Path.Combine(RepositoryRoot(), "types"), DocumentReaderFactory.FromEmbeddedSchemas()));

    public LibraryFolder Folder => _folder.Value;

    public static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "types")) && Directory.Exists(Path.Combine(dir.FullName, "Architecture")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}

/// <summary>The connections seen by the package catalogue.</summary>
internal sealed class FakeConnections : IPackageConnections
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _inUse = new(StringComparer.Ordinal);
    private readonly List<(string Package, string Version, string Reason)> _stopped = [];

    public IReadOnlyList<(string Package, string Version, string Reason)> Stopped
    {
        get
        {
            lock (_lock)
            {
                return [.. _stopped];
            }
        }
    }

    public void Use(string id, string version)
    {
        lock (_lock)
        {
            _inUse.Add($"{id}@{version}");
        }
    }

    public void Release(string id, string version)
    {
        lock (_lock)
        {
            _inUse.Remove($"{id}@{version}");
        }
    }

    public bool IsInUse(PackageId id, SemVersion version)
    {
        lock (_lock)
        {
            return _inUse.Contains($"{id.Value}@{version}");
        }
    }

    public Task StopForPackageAsync(PackageId id, SemVersion version, string reason, CancellationToken ct)
    {
        lock (_lock)
        {
            _stopped.Add((id.Value, version.ToString(), reason));
        }

        return Task.CompletedTask;
    }
}

/// <summary>A change source driven by the test.</summary>
internal sealed class ManualChangeSource : IFolderChangeSource, IFolderChangeSourceFactory
{
    public event Action? Changed;

    public event Action<Exception?>? Overflowed;

    public bool Started { get; private set; }

    public IFolderChangeSource Create(string folder) => this;

    public void Start() => Started = true;

    public void RaiseChanged() => Changed?.Invoke();

    public void RaiseOverflow() => Overflowed?.Invoke(new InternalBufferOverflowException("test"));

    public void Dispose()
    {
    }
}
