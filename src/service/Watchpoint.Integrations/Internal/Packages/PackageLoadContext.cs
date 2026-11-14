using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Watchpoint.Integrations.Internal.Packages;

/// <summary>
/// The collectible load context of one package version (spec IRT §5, ADR-003). Shared assemblies come from the host
/// (same type identity); everything else is private to the package and resolved from its <c>lib/</c> folder.
/// </summary>
internal sealed class PackageLoadContext : AssemblyLoadContext
{
    private readonly string _libDir;
    private readonly string _nativeDir;
    private readonly string _mainAssemblyPath;
    private readonly AssemblyDependencyResolver? _resolver;

    /// <summary>Creates the context of the package unpacked in <paramref name="packageDir"/>.</summary>
    /// <param name="name">Context name (<c>package@version</c>): reported when a crash is attributed.</param>
    /// <param name="packageDir">The verified copy (<c>&lt;cache&gt;/&lt;id&gt;/&lt;version&gt;</c>).</param>
    /// <param name="mainAssembly">Simple name of the main assembly (<c>lib/&lt;main&gt;.dll</c>).</param>
    public PackageLoadContext(string name, string packageDir, string mainAssembly)
        : base(name, isCollectible: true)
    {
        _libDir = Path.Combine(Path.GetFullPath(packageDir), "lib");
        _nativeDir = Path.Combine(Path.GetFullPath(packageDir), "native");
        _mainAssemblyPath = Path.Combine(_libDir, mainAssembly + ".dll");

        // With <main>.deps.json the resolver follows it; without, it probes the main assembly's folder.
        _resolver = File.Exists(_mainAssemblyPath) ? new AssemblyDependencyResolver(_mainAssemblyPath) : null;
    }

    /// <summary>Loads an assembly of the package by simple name; <see langword="null"/> when <c>lib/&lt;name&gt;.dll</c> does not exist.</summary>
    public Assembly? LoadPackageAssembly(string simpleName)
    {
        var path = Path.Combine(_libDir, simpleName + ".dll");
        return IsInLib(path) && File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }

    /// <summary>Path of the native library <paramref name="name"/> for the current platform, or <see langword="null"/>.</summary>
    public string? ResolveNativeLibraryPath(string name)
    {
        if (string.IsNullOrEmpty(name) || name.AsSpan().ContainsAny('/', '\\') || name.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var rid in SharedAssemblies.CurrentRuntimeIdentifiers)
        {
            foreach (var file in NativeFileNames(name))
            {
                var path = Path.Combine(_nativeDir, rid, file);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return _resolver?.ResolveUnmanagedDllToPath(name);
    }

    /// <inheritdoc/>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (SharedAssemblies.IsShared(assemblyName.Name))
        {
            return null; // Default context: the host's copy.
        }

        var path = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (path is null && assemblyName.Name is { } name)
        {
            var candidate = Path.Combine(_libDir, name + ".dll");
            path = IsInLib(candidate) && File.Exists(candidate) ? candidate : null;
        }

        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <inheritdoc/>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
        ResolveNativeLibraryPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;

    private bool IsInLib(string path) =>
        Path.GetFullPath(path).StartsWith(_libDir + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static IEnumerable<string> NativeFileNames(string name)
    {
        yield return name;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return name + ".dll";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "lib" + name + ".dylib";
            yield return name + ".dylib";
        }
        else
        {
            yield return "lib" + name + ".so";
            yield return name + ".so";
        }
    }
}
