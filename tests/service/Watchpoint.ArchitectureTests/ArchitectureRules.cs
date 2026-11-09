using System.Text.RegularExpressions;
using NetArchTest.Rules;

namespace Watchpoint.ArchitectureTests;

/// <summary>
/// The architecture rules of TST §6. Type rules (ARCH-4, ARCH-8) use NetArchTest; reference rules read assembly
/// metadata directly so that matches are exact. Each rule returns its violations (empty when the rule holds).
/// </summary>
internal static partial class ArchitectureRules
{
    public const string Kernel = "Watchpoint.Kernel";
    public const string Host = "Watchpoint.Host";
    public const string IntegrationAbstractions = "Watchpoint.Integration.Abstractions";
    public const string IntegrationSdk = "Watchpoint.Integration.Sdk";
    private const string LoggingAbstractions = "Microsoft.Extensions.Logging.Abstractions";

    private static readonly HashSet<string> _compositionRoots = new(StringComparer.Ordinal)
    {
        Host, "Watchpoint.IntegrationHost", "Watchpoint.ScriptHost", "Watchpoint.Ctl",
    };

    private static readonly string[] _efProviderPrefixes =
    [
        "Npgsql.EntityFrameworkCore.", "Microsoft.EntityFrameworkCore.SqlServer", "Microsoft.EntityFrameworkCore.Sqlite",
        "Microsoft.EntityFrameworkCore.InMemory", "Microsoft.EntityFrameworkCore.Cosmos", "Pomelo.EntityFrameworkCore.", "Oracle.EntityFrameworkCore",
    ];

    /// <summary>Gets whether the assembly is a Watchpoint assembly.</summary>
    public static bool IsWatchpoint(string name) => name.StartsWith("Watchpoint.", StringComparison.Ordinal);

    /// <summary>Gets whether the assembly is a connector package (<c>Watchpoint.Integration.&lt;Name&gt;</c>).</summary>
    public static bool IsConnector(string name) =>
        name.StartsWith("Watchpoint.Integration.", StringComparison.Ordinal)
        && name is not IntegrationAbstractions and not IntegrationSdk and not "Watchpoint.Integration.Testing"
        && !name.EndsWith(".Engine", StringComparison.Ordinal);

    /// <summary>Gets whether the assembly is a service module (discovered by name; contracts and hosts excluded).</summary>
    public static bool IsModule(string name) =>
        IsWatchpoint(name)
        && name != Kernel
        && !_compositionRoots.Contains(name)
        && !name.StartsWith("Watchpoint.Integration.", StringComparison.Ordinal)
        && !name.StartsWith("Watchpoint.Analyzers", StringComparison.Ordinal)
        && !name.EndsWith(".Abstractions", StringComparison.Ordinal)
        && !name.EndsWith(".Testing", StringComparison.Ordinal)
        && !name.StartsWith("Watchpoint.Kernel.", StringComparison.Ordinal);

    /// <summary>ARCH-1: <c>Watchpoint.Kernel</c> depends on no other Watchpoint assembly.</summary>
    public static IReadOnlyList<string> Arch1(IReadOnlyList<ArchAssembly> assemblies) =>
        [.. assemblies.Where(a => a.Name == Kernel).SelectMany(a => a.References.Where(IsWatchpoint).Select(r => $"{a.Name} references {r}"))];

    /// <summary>ARCH-2: no assembly depends on <c>Watchpoint.Host</c>.</summary>
    public static IReadOnlyList<string> Arch2(IReadOnlyList<ArchAssembly> assemblies) =>
        [.. assemblies.Where(a => a.References.Contains(Host)).Select(a => $"{a.Name} references {Host}")];

    /// <summary>
    /// ARCH-3: a module uses another module only through types in namespaces ending with <c>.Contracts</c>
    /// (or through that module's <c>.Abstractions</c> assembly, which is not a module).
    /// </summary>
    public static IReadOnlyList<string> Arch3(IReadOnlyList<ArchAssembly> assemblies)
    {
        var modules = assemblies.Select(a => a.Name).Where(IsModule).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. from a in assemblies
               where IsModule(a.Name)
               from t in a.TypeReferences
               where modules.Contains(t.Assembly) && t.Assembly != a.Name && !IsContractNamespace(t.Namespace)
               select $"{a.Name} uses {t}, which is not in a .Contracts namespace",
        ];
    }

    /// <summary>ARCH-4: types in <c>*.Internal</c> namespaces are not public.</summary>
    public static IReadOnlyList<string> Arch4(IReadOnlyList<ArchAssembly> assemblies) =>
        Failing(assemblies, types => types.That().ResideInNamespaceMatching(InternalNamespace().ToString()).ShouldNot().BePublic().GetResult(), "is public in an Internal namespace");

    /// <summary>ARCH-5: only <c>Watchpoint.Storage*</c> (and <c>Watchpoint.MigrationsDesign</c>) reference EF Core provider packages.</summary>
    public static IReadOnlyList<string> Arch5(IReadOnlyList<ArchAssembly> assemblies) =>
    [
        .. from a in assemblies
           where !a.Name.StartsWith("Watchpoint.Storage", StringComparison.Ordinal) && a.Name != "Watchpoint.MigrationsDesign"
           from r in a.References
           where _efProviderPrefixes.Any(p => r.StartsWith(p, StringComparison.Ordinal))
           select $"{a.Name} references EF Core provider {r}",
    ];

    /// <summary>
    /// ARCH-6: <c>Watchpoint.Integration.Abstractions</c> depends only on <c>Watchpoint.Kernel</c>,
    /// <c>Microsoft.Extensions.Logging.Abstractions</c> (INT §2: <c>ILoggerFactory</c> in the connector context) and the BCL.
    /// </summary>
    public static IReadOnlyList<string> Arch6(IReadOnlyList<ArchAssembly> assemblies) =>
    [
        .. from a in assemblies
           where a.Name == IntegrationAbstractions
           from r in a.References
           where r != Kernel && r != LoggingAbstractions && !IsBcl(r)
           select $"{a.Name} references {r}",
    ];

    /// <summary>ARCH-7: connectors reference only Kernel, Integration.Abstractions, Integration.Sdk, and their own Engine among Watchpoint assemblies.</summary>
    public static IReadOnlyList<string> Arch7(IReadOnlyList<ArchAssembly> assemblies) =>
    [
        .. from a in assemblies
           where IsConnector(a.Name)
           from r in a.References
           where IsWatchpoint(r) && r is not Kernel and not IntegrationAbstractions and not IntegrationSdk && r != $"{a.Name}.Engine"
           select $"{a.Name} references {r}",
    ];

    /// <summary>ARCH-8: no type is named <c>*Controller</c> (minimal APIs only).</summary>
    public static IReadOnlyList<string> Arch8(IReadOnlyList<ArchAssembly> assemblies) =>
        Failing(assemblies, types => types.ShouldNot().HaveNameEndingWith("Controller").GetResult(), "is a controller");

    private static bool IsContractNamespace(string ns) =>
        ns.EndsWith(".Contracts", StringComparison.Ordinal) || ns.Contains(".Contracts.", StringComparison.Ordinal);

    private static bool IsBcl(string reference) =>
        reference is "System" or "mscorlib" or "netstandard"
        || reference.StartsWith("System.", StringComparison.Ordinal)
        || reference.StartsWith("Microsoft.Win32.", StringComparison.Ordinal);

    private static List<string> Failing(IReadOnlyList<ArchAssembly> assemblies, Func<Types, TestResult> rule, string problem)
    {
        var violations = new List<string>();
        foreach (var assembly in assemblies)
        {
            var result = rule(Types.FromFile(assembly.Path));
            if (!result.IsSuccessful)
            {
                violations.AddRange(result.FailingTypeNames.Select(t => $"{assembly.Name}: {t} {problem}"));
            }
        }

        return violations;
    }

    [GeneratedRegex(@"\.Internal(\.|$)")]
    private static partial Regex InternalNamespace();
}
