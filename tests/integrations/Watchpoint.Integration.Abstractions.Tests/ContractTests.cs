using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;
using Watchpoint.Integration.Abstractions.Model;
using Xunit;

namespace Watchpoint.Integration.Abstractions.Tests;

public sealed partial class ContractTests
{
    [Theory]
    [InlineData("^1.0", "1.0", true)]
    [InlineData("^1.0", "1.3", true)]
    [InlineData("^1.0", "2.0", false)]
    [InlineData("^1.3", "1.2", false)]
    [InlineData("^1.0", "0.9", false)]
    [InlineData("1.2", "1.2", true)]
    [InlineData("1.2", "1.3", false)]
    [InlineData("^1.0", "1", false)]
    [InlineData("^1.0", "1.x", false)]
    [InlineData("^1.0", "-1.0", false)]
    [InlineData("^1.0", " 1.0", false)]
    [InlineData("~1.0", "1.0", false)]
    [InlineData("^", "1.0", false)]
    [InlineData(null, "1.0", false)]
    [InlineData("^1.0", null, false)]
    public void INT_U_008_ContractVersion_compatibility(string? requirement, string? version, bool expected) =>
        ContractVersion.Satisfies(requirement, version).ShouldBe(expected);

    [Fact]
    public void INT_U_008_ContractVersion_parsing_and_current()
    {
        ContractVersion.TryParse("1.13", out var major, out var minor).ShouldBeTrue();
        (major, minor).ShouldBe((1, 13));
        ContractVersion.TryParse("1.0.0", out _, out _).ShouldBeFalse();
        ContractVersion.TryParse("", out _, out _).ShouldBeFalse();
        ContractVersion.TryParse(ContractVersion.Current, out _, out _).ShouldBeTrue();

        ContractVersion.IsSupported("^1.0").ShouldBeTrue();
        ContractVersion.IsSupported("^1.1").ShouldBeFalse();     // newer minor than implemented
        ContractVersion.IsSupported("^2.0").ShouldBeFalse();     // unknown major
    }

    [Fact]
    public void INT_U_009_Public_API_snapshot_unchanged_except_additions()
    {
        // RS0016 (undeclared public symbol) and RS0017 (declared symbol removed) fail the build
        // (Microsoft.CodeAnalysis.PublicApiAnalyzers): removals and changes are build errors, additions go to
        // PublicAPI.Unshipped.txt. This test keeps the analyzer wired and checks every public type is declared.
        var dir = Path.Combine(RepositoryRoot(), "src", "integrations", "Watchpoint.Integration.Abstractions");
        File.ReadAllText(Path.Combine(dir, "Watchpoint.Integration.Abstractions.csproj"))
            .ShouldContain("Microsoft.CodeAnalysis.PublicApiAnalyzers");

        var shipped = File.ReadAllLines(Path.Combine(dir, "PublicAPI.Shipped.txt"));
        var unshipped = File.ReadAllLines(Path.Combine(dir, "PublicAPI.Unshipped.txt"));
        shipped[0].ShouldBe("#nullable enable");
        unshipped[0].ShouldBe("#nullable enable");
        unshipped.Skip(1).ShouldAllBe(line => !line.StartsWith("*REMOVED*", StringComparison.Ordinal));
        var declared = shipped.Concat(unshipped).Skip(1).ToList();

        var types = typeof(IConnector).Assembly.GetExportedTypes();
        types.Length.ShouldBeGreaterThan(40);
        foreach (var type in types)
        {
            var name = ApiName(type);
            declared.ShouldContain(line => line.StartsWith(name, StringComparison.Ordinal), name);
        }
    }

    [Fact]
    public void INT_U_010_Abstractions_references_only_Kernel_and_logging_abstractions()
    {
        var references = typeof(IConnector).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        references.ShouldContain("Watchpoint.Kernel");
        references.Where(r => r.StartsWith("Watchpoint.", StringComparison.Ordinal)).ShouldBe(["Watchpoint.Kernel"]);
        references.ShouldAllBe(r => r == "Watchpoint.Kernel" || r == "Microsoft.Extensions.Logging.Abstractions"
            || r == "System" || r == "netstandard" || r.StartsWith("System.", StringComparison.Ordinal));
    }

    [Fact]
    public void INT_U_010_Abstractions_has_no_mutable_static_state()
    {
        // Rule L1: instances never share mutable static state; the contract itself has none either.
        var fields = typeof(IConnector).Assembly.GetTypes()
            .Where(t => !t.Name.Contains('<', StringComparison.Ordinal) && t.Namespace?.StartsWith("Watchpoint", StringComparison.Ordinal) == true
                && !t.Name.Contains("JsonContext", StringComparison.Ordinal))
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(f => !f.IsLiteral && !f.IsInitOnly && !f.Name.Contains('<', StringComparison.Ordinal))
            .Select(f => f.DeclaringType!.Name + "." + f.Name)
            .ToList();
        fields.ShouldBeEmpty();
        typeof(ConnectorCommandResult).ShouldNotBeNull();
    }

    private static string ApiName(Type type)
    {
        var name = (type.FullName ?? type.Name).Replace('+', '.');
        if (!type.IsGenericTypeDefinition)
        {
            return name;
        }

        return GenericArity().Replace(name, string.Empty) + "<" + string.Join(", ", type.GetGenericArguments().Select(a => a.Name)) + ">";
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex("`\\d+")]
    private static partial Regex GenericArity();
}
