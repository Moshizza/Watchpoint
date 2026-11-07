using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Xunit;

namespace Watchpoint.ArchitectureTests;

/// <summary>The rules detect violations: fixture assemblies are compiled in memory and written to a temporary folder.</summary>
[Trait("layer", "I")]
[Trait("spec", "TST")]
public sealed class RuleViolationTests : IDisposable
{
    private static readonly MetadataReference[] _bcl =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll" or "netstandard.dll")
            .Select(p => MetadataReference.CreateFromFile(p)),
    ];

    private readonly string _directory = Directory.CreateTempSubdirectory("wp-arch-").FullName;

    [Fact]
    public void TST_I_009_ARCH_3_detects_a_violation()
    {
        var provider = Compile("Watchpoint.FixtureProvider", """
            namespace Watchpoint.FixtureProvider.Internal { public sealed class Helper { public static int Value() => 1; } }
            namespace Watchpoint.FixtureProvider.Contracts { public interface IProviderApi { int Get(); } }
            """);
        var offender = Compile("Watchpoint.FixtureOffender", """
            namespace Watchpoint.FixtureOffender { public static class UsesInternals { public static int Get() => Watchpoint.FixtureProvider.Internal.Helper.Value(); } }
            """, provider);
        var compliant = Compile("Watchpoint.FixtureCompliant", """
            namespace Watchpoint.FixtureCompliant { public sealed class UsesContract(Watchpoint.FixtureProvider.Contracts.IProviderApi api) { public int Get() => api.Get(); } }
            """, provider);
        var assemblies = new[] { provider, offender, compliant }.Select(c => ArchAssembly.Read(c.Path)).ToList();

        var violations = ArchitectureRules.Arch3(assemblies);

        var violation = violations.ShouldHaveSingleItem();
        violation.ShouldStartWith("Watchpoint.FixtureOffender uses Watchpoint.FixtureProvider.Internal.Helper");
        ArchitectureRules.Arch4(assemblies).ShouldHaveSingleItem().ShouldContain("Watchpoint.FixtureProvider.Internal.Helper");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private (string Path, MetadataReference Reference) Compile(string name, string source, params (string Path, MetadataReference Reference)[] references)
    {
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            [.. _bcl, .. references.Select(r => r.Reference)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var path = Path.Combine(_directory, name + ".dll");
        var result = compilation.Emit(path);
        result.Success.ShouldBeTrue(string.Join(Environment.NewLine, result.Diagnostics));
        return (path, MetadataReference.CreateFromFile(path));
    }
}
