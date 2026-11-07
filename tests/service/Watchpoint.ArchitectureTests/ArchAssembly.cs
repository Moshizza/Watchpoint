using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Watchpoint.ArchitectureTests;

/// <summary>An assembly under test, read from its file with System.Reflection.Metadata (never loaded).</summary>
internal sealed record ArchAssembly(string Name, string Path, IReadOnlyList<string> References, IReadOnlyList<TypeRef> TypeReferences)
{
    public static ArchAssembly Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var name = md.GetString(md.GetAssemblyDefinition().Name);
        var references = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
        var typeRefs = new List<TypeRef>();
        foreach (var handle in md.TypeReferences)
        {
            var typeRef = md.GetTypeReference(handle);
            var scope = typeRef.ResolutionScope;
            while (scope.Kind == HandleKind.TypeReference)
            {
                // Nested type: the outer type carries the namespace and assembly scope.
                var outer = md.GetTypeReference((TypeReferenceHandle)scope);
                typeRef = outer;
                scope = outer.ResolutionScope;
            }

            if (scope.Kind == HandleKind.AssemblyReference)
            {
                var assembly = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
                typeRefs.Add(new TypeRef(assembly, md.GetString(typeRef.Namespace), md.GetString(typeRef.Name)));
            }
        }

        return new ArchAssembly(name, path, references, typeRefs.Distinct().ToList());
    }
}

/// <summary>A type referenced from another assembly.</summary>
internal sealed record TypeRef(string Assembly, string Namespace, string Name)
{
    public override string ToString() => $"{Namespace}.{Name} ({Assembly})";
}
