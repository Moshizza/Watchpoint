using System.Text.RegularExpressions;
using Shouldly;
using Watchpoint.Kernel.Results;
using Xunit;

namespace Watchpoint.Kernel.Tests;

public sealed partial class PublicApiTests
{
    [Fact]
    public void K_U_055_Public_API_snapshot()
    {
        // Member-level enforcement is done at build time by Microsoft.CodeAnalysis.PublicApiAnalyzers
        // (RS0016: undeclared public symbol, RS0017: declared symbol removed, both errors). This test makes sure the
        // analyzer stays wired and that every public type is in the snapshot.
        var kernelDir = Path.Combine(RepositoryRoot(), "src", "service", "Watchpoint.Kernel");
        var project = File.ReadAllText(Path.Combine(kernelDir, "Watchpoint.Kernel.csproj"));
        project.ShouldContain("Microsoft.CodeAnalysis.PublicApiAnalyzers");

        var shipped = File.ReadAllLines(Path.Combine(kernelDir, "PublicAPI.Shipped.txt"));
        var unshipped = File.ReadAllLines(Path.Combine(kernelDir, "PublicAPI.Unshipped.txt"));
        shipped[0].ShouldBe("#nullable enable");
        unshipped[0].ShouldBe("#nullable enable");
        var declared = shipped.Concat(unshipped).Skip(1).ToHashSet(StringComparer.Ordinal);

        var publicTypes = typeof(Result).Assembly.GetExportedTypes();
        publicTypes.Length.ShouldBeGreaterThan(50);
        foreach (var type in publicTypes)
        {
            var name = ApiName(type);
            declared.ShouldContain(line => line.StartsWith(name, StringComparison.Ordinal)
                || line.Contains(" " + name, StringComparison.Ordinal), name);
        }
    }

    private static string ApiName(Type type)
    {
        var name = (type.FullName ?? type.Name).Replace('+', '.');
        if (!type.IsGenericTypeDefinition)
        {
            return name;
        }

        var args = string.Join(", ", type.GetGenericArguments().Select(a => a.Name));
        return GenericArity().Replace(name, string.Empty) + "<" + args + ">";
    }

    private static string RepositoryRoot()
    {
        // From the test output folder (CallerFilePath is path-mapped in CI builds).
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
