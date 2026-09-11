using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace NOBA.Archive.Architecture.Tests;

public sealed record MemberUse(string TypeNamespace, string TypeName, string MemberName)
{
    public string FullTypeName => string.IsNullOrEmpty(TypeNamespace) ? TypeName : $"{TypeNamespace}.{TypeName}";
}

/// <summary>
/// Reads a compiled assembly's metadata without loading it.
/// </summary>
/// <remarks>
/// Assembly references answer the layering question; type and member references answer the
/// member-level questions (<c>System.IO.File</c>, <c>Console</c>, <c>.Result</c>) that
/// <c>Assembly.GetReferencedAssemblies()</c> cannot see, because those live in method bodies rather
/// than in the reference list. <see cref="System.Reflection.Metadata"/> ships in the shared framework,
/// so this needs no third-party dependency.
/// </remarks>
public sealed class AssemblyProbe
{
    private AssemblyProbe(
        string name,
        ImmutableArray<string> assemblyReferences,
        ImmutableArray<string> typeReferences,
        ImmutableArray<MemberUse> memberReferences)
    {
        Name = name;
        AssemblyReferences = assemblyReferences;
        TypeReferences = typeReferences;
        MemberReferences = memberReferences;
    }

    public string Name { get; }

    public ImmutableArray<string> AssemblyReferences { get; }

    /// <summary>Fully-qualified names of every type this assembly refers to.</summary>
    public ImmutableArray<string> TypeReferences { get; }

    public ImmutableArray<MemberUse> MemberReferences { get; }

    /// <summary>
    /// Project name to output file name, for the projects whose assembly name differs. The CLI ships as
    /// <c>noba-ar</c> because that is what a user types.
    /// </summary>
    private static readonly Dictionary<string, string> OutputFileNames = new(StringComparer.Ordinal)
    {
        ["NOBA.Archive.Cli"] = "noba-ar",
    };

    public static string LocateAssembly(string simpleName)
    {
        var fileName = OutputFileNames.TryGetValue(simpleName, out var mapped) ? mapped : simpleName;
        var path = Path.Combine(AppContext.BaseDirectory, fileName + ".dll");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"'{fileName}.dll' is not in the test output directory. " +
                "The architecture tests must reference every project they police.",
                path);
        }

        return path;
    }

    public static AssemblyProbe Load(string simpleName)
    {
        using var stream = File.OpenRead(LocateAssembly(simpleName));
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        var assemblyRefs = ImmutableArray.CreateBuilder<string>();
        foreach (var handle in reader.AssemblyReferences)
        {
            assemblyRefs.Add(reader.GetString(reader.GetAssemblyReference(handle).Name));
        }

        var typeRefs = ImmutableArray.CreateBuilder<string>();
        foreach (var handle in reader.TypeReferences)
        {
            var typeRef = reader.GetTypeReference(handle);
            var ns = reader.GetString(typeRef.Namespace);
            var name = reader.GetString(typeRef.Name);
            typeRefs.Add(string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}");
        }

        var memberRefs = ImmutableArray.CreateBuilder<MemberUse>();
        foreach (var handle in reader.MemberReferences)
        {
            var memberRef = reader.GetMemberReference(handle);
            var memberName = reader.GetString(memberRef.Name);

            // Only parents that are plain type references can be resolved cheaply; a generic
            // instantiation arrives as a TypeSpecification and is skipped. The rules below are
            // written to stay meaningful under that limitation.
            if (memberRef.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var parent = reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
            memberRefs.Add(new MemberUse(
                reader.GetString(parent.Namespace),
                reader.GetString(parent.Name),
                memberName));
        }

        return new AssemblyProbe(
            simpleName,
            assemblyRefs.ToImmutable(),
            typeRefs.ToImmutable(),
            memberRefs.ToImmutable());
    }
}
