using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Watchpoint.Integration.Abstractions;
using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Documents;

namespace Watchpoint.Integrations.Internal.Packages;

/// <summary>
/// The load contexts of the active packages: one per (package, version), shared by its in-process connections and
/// unloaded when the last lease is returned (spec IRT §5). Thread-safe.
/// </summary>
internal sealed class PackageLoadContextPool(TimeProvider time, ILogger<PackageLoadContextPool> logger)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly List<(string Name, WeakReference Context, DateTimeOffset UnloadedAt)> _unloaded = [];

    /// <summary>Takes a lease on the context of a package version, creating the context on first use.</summary>
    /// <param name="id">The package.</param>
    /// <param name="version">The version.</param>
    /// <param name="packageDir">The verified copy in the cache.</param>
    /// <param name="mainAssembly">Simple name of the main assembly.</param>
    public PackageLease Acquire(PackageId id, SemVersion version, string packageDir, string mainAssembly)
    {
        var key = Key(id, version);
        lock (_lock)
        {
            if (!_slots.TryGetValue(key, out var slot))
            {
                slot = new Slot(new PackageLoadContext(key, packageDir, mainAssembly));
                _slots[key] = slot;
            }

            slot.Leases++;
            return new PackageLease(this, key, slot.Context);
        }
    }

    /// <summary>Gets whether a context is loaded for the package version (a connection uses it).</summary>
    public bool IsLoaded(PackageId id, SemVersion version)
    {
        lock (_lock)
        {
            return _slots.ContainsKey(Key(id, version));
        }
    }

    /// <summary>Names of the contexts unloaded for longer than <paramref name="olderThan"/> and still not collected (HLT).</summary>
    public IReadOnlyList<string> NotCollected(TimeSpan olderThan)
    {
        var limit = time.GetUtcNow() - olderThan;
        lock (_lock)
        {
            _unloaded.RemoveAll(u => !u.Context.IsAlive);
            return [.. _unloaded.Where(u => u.UnloadedAt <= limit).Select(u => u.Name)];
        }
    }

    internal void Release(string key)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(key, out var slot) || --slot.Leases > 0)
            {
                return;
            }

            _slots.Remove(key);
            _unloaded.Add((key, Unload(slot), time.GetUtcNow()));
        }

        IntegrationLog.LoadContextUnloaded(logger, key);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Unload(Slot slot)
    {
        var context = slot.Context;
        slot.Context = null!;
        context.Unload();
        return new WeakReference(context);
    }

    private static string Key(PackageId id, SemVersion version) => $"{id.Value}@{version}";

    private sealed class Slot(PackageLoadContext context)
    {
        public PackageLoadContext Context { get; set; } = context;

        public int Leases { get; set; }
    }
}

/// <summary>A lease on a package's load context; dispose it when the connection no longer needs the package.</summary>
internal sealed class PackageLease : IDisposable
{
    private readonly PackageLoadContextPool _pool;
    private readonly string _key;
    private PackageLoadContext? _context;

    internal PackageLease(PackageLoadContextPool pool, string key, PackageLoadContext context)
    {
        _pool = pool;
        _key = key;
        _context = context;
    }

    /// <summary>Gets the context.</summary>
    public PackageLoadContext Context => _context ?? throw new ObjectDisposedException(nameof(PackageLease));

    /// <summary>Creates an instance of a connector class (<c>Assembly::Namespace.Type</c>, as in the manifest).</summary>
    /// <exception cref="InvalidOperationException">The class is missing or is not a connector (the code check prevents this for verified packages).</exception>
    public IConnector CreateConnector(string connectorClass)
    {
        var (assemblyName, typeName) = ConnectorCodeChecker.SplitClass(connectorClass);
        var type = Context.LoadPackageAssembly(assemblyName)?.GetType(typeName, throwOnError: false)
            ?? throw new InvalidOperationException($"Connector class {connectorClass} not found.");
        return Activator.CreateInstance(type) as IConnector
            ?? throw new InvalidOperationException($"{connectorClass} does not implement IConnector.");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _context, null) is not null)
        {
            _pool.Release(_key);
        }
    }
}
