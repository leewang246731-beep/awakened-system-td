using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace EraCore.Tests.Architecture;

/// <summary>
/// Shared engine behind the D-01 architecture guard tests. Reads the
/// AssemblyReferences table straight from PE metadata, so any assembly
/// <em>file</em> can be audited by path — including GodotSharp.dll and
/// EraTD.dll, whose dependency closure is not resolvable from the test host.
/// Nothing is loaded into the AppDomain and nothing is executed; a missing or
/// unreadable file surfaces as an exception, never as an empty result.
/// Extracted in review round-1 (REV-20261013-wave0-round1) so the guard can
/// be pointed at arbitrary assemblies for negative controls.
/// </summary>
public static class AssemblyGuard
{
    /// <summary>
    /// Returns every forbidden token actually referenced by the assembly at
    /// <paramref name="assemblyPath"/>. A token matches when it appears as a
    /// case-insensitive substring of a referenced assembly name — the same
    /// Contains() semantics the guard has always used.
    /// </summary>
    public static IReadOnlyList<string> FindForbiddenReferences(
        string assemblyPath, IReadOnlyList<string> forbidden)
    {
        var hits = new List<string>();
        foreach (var referenceName in ReferenceNames(assemblyPath))
        {
            foreach (var token in forbidden)
            {
                if (referenceName.Contains(token, StringComparison.OrdinalIgnoreCase)
                    && !hits.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    hits.Add(token);
                }
            }
        }

        return hits;
    }

    /// <summary>Referenced assembly names of the assembly file, in table order.</summary>
    public static IReadOnlyList<string> ReferenceNames(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException(
                $"Guard target assembly not found: '{assemblyPath}'.", assemblyPath);
        }

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();
        var names = new List<string>();
        foreach (var handle in reader.AssemblyReferences)
        {
            names.Add(reader.GetString(reader.GetAssemblyReference(handle).Name));
        }

        return names;
    }
}
