using System.Reflection;
using System.Runtime.Loader;

namespace Watchpoint.Spikes.ConnectorIsolation.Contract;

/// <summary>IRT §5 load context: collectible; shared assemblies come from the default context.</summary>
public sealed class PackageLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> _shared = new(StringComparer.Ordinal)
    {
        typeof(ISpikeConnector).Assembly.GetName().Name!,
    };

    private readonly AssemblyDependencyResolver _resolver;

    /// <summary>Creates a context for the package whose main assembly is <paramref name="mainAssemblyPath"/>.</summary>
    public PackageLoadContext(string mainAssemblyPath)
        : base($"package:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}", isCollectible: true) =>
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);

    /// <summary>Loads the main assembly and creates the connector type found in it.</summary>
    public static (PackageLoadContext Context, ISpikeConnector Connector) LoadConnector(string mainAssemblyPath)
    {
        var context = new PackageLoadContext(mainAssemblyPath);
        var assembly = context.LoadFromAssemblyPath(mainAssemblyPath);
        var type = assembly.GetTypes().Single(t => typeof(ISpikeConnector).IsAssignableFrom(t) && !t.IsAbstract);
        return (context, (ISpikeConnector)Activator.CreateInstance(type)!);
    }

    /// <inheritdoc/>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name && (_shared.Contains(name) || name.StartsWith("System.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.", StringComparison.Ordinal)))
        {
            return null; // fall back to the default context: identical types on both sides of the boundary
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <inheritdoc/>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
