namespace Watchpoint.ArchitectureTests;

/// <summary>The Watchpoint assemblies of the solution, discovered by name in the test output directory.</summary>
internal static class SolutionAssemblies
{
    private static readonly Lazy<IReadOnlyList<ArchAssembly>> _all = new(Discover);

    public static IReadOnlyList<ArchAssembly> All => _all.Value;

    private static IReadOnlyList<ArchAssembly> Discover() =>
    [
        .. Directory.EnumerateFiles(AppContext.BaseDirectory, "Watchpoint.*.dll")
            .Select(ArchAssembly.Read)
            .Where(a => a.Name != "Watchpoint.ArchitectureTests" && !a.Name.StartsWith("Watchpoint.Testing", StringComparison.Ordinal))
            .OrderBy(a => a.Name, StringComparer.Ordinal),
    ];
}
