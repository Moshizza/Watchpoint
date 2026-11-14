using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace Watchpoint.Integrations.Internal.Packages;

/// <summary>
/// Assemblies always loaded from the host so that their types are identical on both sides of a load context
/// (spec IRT §5), and the runtime identifiers of the current platform.
/// </summary>
internal static class SharedAssemblies
{
    /// <summary>The contract assemblies: never loaded from a package, even when it ships a copy.</summary>
    public static FrozenSet<string> Contract { get; } = new[]
    {
        "Watchpoint.Kernel",
        "Watchpoint.Integration.Abstractions",
        "Watchpoint.Integration.Sdk",
        "NodaTime",
        "Microsoft.Extensions.Logging.Abstractions",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<FrozenSet<string>> _hostAssemblies = new(ReadHostAssemblies);

    /// <summary>Gets whether <paramref name="assemblyName"/> must come from the host.</summary>
    public static bool IsShared(string? assemblyName) =>
        assemblyName is not null
        && (Contract.Contains(assemblyName) || (IsFrameworkName(assemblyName) && _hostAssemblies.Value.Contains(assemblyName)));

    /// <summary>The runtime identifiers naming the current platform, most specific first (e.g. <c>linux-x64</c>).</summary>
    public static IReadOnlyList<string> CurrentRuntimeIdentifiers { get; } = RuntimeIdentifiers();

    private static bool IsFrameworkName(string name) =>
        name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
        || name.Equals("System", StringComparison.OrdinalIgnoreCase) || name.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
        || name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase);

    private static FrozenSet<string> ReadHostAssemblies() =>
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static string[] RuntimeIdentifiers()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : "linux";
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return [.. new[] { RuntimeInformation.RuntimeIdentifier, $"{os}-{architecture}" }.Distinct(StringComparer.Ordinal)];
    }
}
