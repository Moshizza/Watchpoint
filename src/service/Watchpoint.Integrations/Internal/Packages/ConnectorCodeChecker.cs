using System.Reflection;
using System.Runtime.CompilerServices;
using Watchpoint.Integration.Abstractions;
using Watchpoint.Integration.Abstractions.Features;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Model.Documents.Library;

namespace Watchpoint.Integrations.Internal.Packages;

/// <summary>Result of the code check of a package.</summary>
/// <param name="Problems">Errors (<c>irt.package.connector_invalid</c>) and warnings (<c>shared_assembly_shadowed</c>).</param>
/// <param name="HasLocator">Per server type: whether the connector implements <see cref="IServerLocator"/>.</param>
/// <param name="Context">Weak reference to the throw-away load context (collected once unloaded).</param>
internal sealed record CodeCheckResult(IReadOnlyList<DocumentProblem> Problems, IReadOnlyDictionary<string, bool> HasLocator, WeakReference Context);

/// <summary>
/// Code check (spec IRT §4.3 step 7): in a throw-away collectible load context, each connector class exists, is public,
/// non-abstract, implements <see cref="IConnector"/>, has a public parameterless constructor and a
/// <see cref="ConnectorAttribute"/> matching the manifest. Only metadata is read: no package code runs.
/// </summary>
internal static class ConnectorCodeChecker
{
    public static CodeCheckResult Check(string packageDir, ManifestDocument manifest)
    {
        var problems = new List<DocumentProblem>();
        var locators = new Dictionary<string, bool>(StringComparer.Ordinal);

        var lib = Path.Combine(packageDir, "lib");
        if (Directory.Exists(lib))
        {
            foreach (var file in Directory.EnumerateFiles(lib, "*.dll").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal))
            {
                if (SharedAssemblies.IsShared(file))
                {
                    problems.Add(PackageProblems.Warning(IntegrationErrors.PackageSharedAssemblyShadowed, $"lib/{file}.dll", ("assembly", file)));
                }
            }
        }

        // The reflection objects stay in the non-inlined frame, so that nothing on this stack roots the context.
        var context = Inspect(packageDir, manifest, problems, locators);
        return new CodeCheckResult(problems, locators, context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Inspect(string packageDir, ManifestDocument manifest, List<DocumentProblem> problems, Dictionary<string, bool> locators)
    {
        var mainAssembly = manifest.Connectors.Count > 0 ? SplitClass(manifest.Connectors[0].Class).Assembly : "none";
        var context = new PackageLoadContext($"check:{manifest.Id}@{manifest.Version}", packageDir, mainAssembly);
        try
        {
            for (var i = 0; i < manifest.Connectors.Count; i++)
            {
                var connector = manifest.Connectors[i];
                var reason = InspectConnector(context, connector, out var detail, out var hasLocator);
                if (reason is null)
                {
                    locators[connector.ServerType] = hasLocator;
                }
                else
                {
                    problems.Add(new DocumentProblem(ProblemSeverity.Error, PackageProblems.ManifestFile, $"/connectors/{i}/class",
                        Kernel.Ids.ErrorCode.FromTrusted(IntegrationErrors.PackageConnectorInvalid),
                        new Dictionary<string, object?> { ["class"] = connector.Class, ["reason"] = reason, ["detail"] = detail }));
                }
            }
        }
        finally
        {
            context.Unload();
        }

        return new WeakReference(context);
    }

    private static string? InspectConnector(PackageLoadContext context, ConnectorDefinitionDoc connector, out string? detail, out bool hasLocator)
    {
        detail = null;
        hasLocator = false;
        var (assemblyName, typeName) = SplitClass(connector.Class);
        try
        {
            var assembly = context.LoadPackageAssembly(assemblyName);
            if (assembly is null)
            {
                return "assemblyMissing";
            }

            var type = assembly.GetType(typeName, throwOnError: false);
            if (type is null)
            {
                return "classMissing";
            }

            if (!type.IsVisible)
            {
                return "notPublic";
            }

            if (type.IsAbstract || type.IsInterface)
            {
                return "abstract";
            }

            if (!typeof(IConnector).IsAssignableFrom(type))
            {
                return "notConnector";
            }

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                return "noParameterlessConstructor";
            }

            var attribute = type.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType == typeof(ConnectorAttribute));
            if (attribute is null)
            {
                return "attributeMissing";
            }

            var serverType = attribute.ConstructorArguments.Count == 1 ? attribute.ConstructorArguments[0].Value as string : null;
            if (!string.Equals(serverType, connector.ServerType, StringComparison.Ordinal))
            {
                detail = serverType;
                return "attributeMismatch";
            }

            hasLocator = typeof(IServerLocator).IsAssignableFrom(type);
            return null;
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException or TypeLoadException or ReflectionTypeLoadException)
        {
            detail = ex.Message;
            return "loadFailed";
        }
    }

    /// <summary>Splits <c>Assembly::Namespace.Type</c> (format enforced by the manifest schema).</summary>
    public static (string Assembly, string Type) SplitClass(string connectorClass)
    {
        var parts = connectorClass.Split("::", 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (connectorClass, string.Empty);
    }
}
