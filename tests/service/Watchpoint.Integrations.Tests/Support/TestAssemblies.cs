using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Watchpoint.Integrations.Tests.Support;

/// <summary>Compiles small assemblies for test packages (connectors, private dependencies).</summary>
public static class TestAssemblies
{
    private static readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private static readonly Lazy<IReadOnlyList<MetadataReference>> _platform = new(() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !Path.GetFileName(p).StartsWith("Vendor.", StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))]);

    /// <summary>A connector class body implementing <c>IConnector</c>; <paramref name="members"/> adds members.</summary>
    public static string ConnectorSource(string @namespace, string className, string? serverType, string modifiers = "public sealed", string members = "", bool implement = true) => $$"""
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Watchpoint.Integration.Abstractions;
        using Watchpoint.Integration.Abstractions.Context;
        using Watchpoint.Integration.Abstractions.Model;
        using Watchpoint.Kernel.Results;

        namespace {{@namespace}};

        {{(serverType is null ? string.Empty : $"[Connector(\"{serverType}\")]")}}
        {{modifiers}} class {{className}}{{(implement ? " : IConnector" : string.Empty)}}
        {
            public ValueTask<Result> InitializeAsync(IConnectorContext context, CancellationToken ct) => throw new NotImplementedException();
            public ValueTask<ConnectionTestResult> TestAsync(CancellationToken ct) => throw new NotImplementedException();
            public ValueTask StartAsync(CancellationToken ct) => throw new NotImplementedException();
            public ValueTask StopAsync(CancellationToken ct) => throw new NotImplementedException();
            public ValueTask<InventorySnapshot> DescribeAsync(CancellationToken ct) => throw new NotImplementedException();
            public ValueTask DisposeAsync() => default;
            {{members}}
        }
        """;

    /// <summary>Compiles <paramref name="source"/> into an assembly named <paramref name="assemblyName"/>.</summary>
    public static byte[] Compile(string assemblyName, string source, string version = "1.0.0.0", params byte[][] references) =>
        _cache.GetOrAdd($"{assemblyName}|{version}|{source}|{string.Join(',', references.Select(r => r.Length))}", _ =>
        {
            var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken);
            var attributes = CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{version}\")]", cancellationToken: TestContext.Current.CancellationToken);
            var compilation = CSharpCompilation.Create(assemblyName, [tree, attributes],
                [.. _platform.Value, .. references.Select(r => MetadataReference.CreateFromImage(r))],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
            using var stream = new MemoryStream();
            var result = compilation.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
            if (!result.Success)
            {
                throw new InvalidOperationException("Test assembly does not compile: " + string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            }

            return stream.ToArray();
        });
}
