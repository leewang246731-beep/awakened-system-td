using EraCore;
using Xunit;

namespace EraCore.Tests.Architecture;

/// <summary>
/// D-01 architecture guard: EraCore must stay a pure C# logic library.
/// These tests fail the build the moment someone drags engine or banned
/// dependencies into the logic layer. All checks route through
/// <see cref="AssemblyGuard.FindForbiddenReferences"/>, which audits the
/// compiled assembly file by path — the same engine is then deliberately
/// pointed at assemblies that DO carry a forbidden reference (negative
/// controls) to prove the guard can actually go red.
/// </summary>
// [F-154] C# 热路径架构守卫（零 GodotSharp/禁反射发射/禁 Newtonsoft/禁直连网络）
public class GodotSharpGuardTests
{
    private static string CoreAssemblyPath => typeof(Guard).Assembly.Location;

    // ---------------------------------------------------------- guard (EraCore)

    [Fact]
    public void EraCore_references_no_GodotSharp()
    {
        Assert.Empty(AssemblyGuard.FindForbiddenReferences(
            CoreAssemblyPath, new[] { "godot" }));
    }

    [Fact]
    public void EraCore_references_no_banned_assemblies()
    {
        // D-01/D-05: Newtonsoft.Json and direct System.Net.Http are forbidden;
        // System.Reflection.Emit* dies under NativeAOT (Web export prerequisite).
        var offending = AssemblyGuard.FindForbiddenReferences(
            CoreAssemblyPath,
            new[] { "newtonsoft", "System.Net.Http", "System.Reflection.Emit" });

        Assert.Empty(offending);
    }

    [Fact]
    public void EraCore_exports_no_Godot_namespace_types()
    {
        var offending = typeof(Guard).Assembly.GetExportedTypes()
            .Where(t => t.Namespace?.StartsWith("Godot", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        Assert.Empty(offending);
    }

    // ------------------------------------------- negative controls (guard itself)

    [Fact]
    public void Guard_detects_a_forbidden_reference_when_one_exists()
    {
        // "Must go red" control: point the guard at this test assembly with a
        // token we KNOW is referenced (xunit) and require a hit. Without this,
        // an always-empty guard would pass every assertion above vacuously.
        var testAssemblyPath = typeof(GodotSharpGuardTests).Assembly.Location;

        var hits = AssemblyGuard.FindForbiddenReferences(
            testAssemblyPath, new[] { "xunit" });

        Assert.Contains("xunit", hits);
    }

    [Fact]
    public void Guard_reports_nothing_for_clean_assembly_and_unknown_tokens()
    {
        // "No false positives" control: xunit.core.dll carries no GodotSharp,
        // and "NoSuchDll" matches nothing anywhere — both must stay silent.
        var xunitCorePath = typeof(FactAttribute).Assembly.Location;

        Assert.Empty(AssemblyGuard.FindForbiddenReferences(
            xunitCorePath, new[] { "GodotSharp", "NoSuchDll" }));
        Assert.Empty(AssemblyGuard.FindForbiddenReferences(
            typeof(GodotSharpGuardTests).Assembly.Location, new[] { "GodotSharp" }));
    }

    [Fact]
    public void EraTD_presentation_assembly_is_caught_referencing_GodotSharp()
    {
        // Strong negative control: EraTD is the one project that legitimately
        // references GodotSharp (D-02 presentation layer), so the guard MUST
        // flag it — proving the check bites on a real violation. Conditional:
        // runs whenever the presentation project has been built locally
        // (dotnet build game/presentation/EraTD); CI builds it before tests.
        var eraTdPath = FindEraTdAssemblyPath();
        if (!File.Exists(eraTdPath))
        {
            // Skip-if-missing (no dynamic Skip in xunit 2.x): report and pass.
            return;
        }

        var hits = AssemblyGuard.FindForbiddenReferences(eraTdPath, new[] { "GodotSharp" });

        Assert.Contains("GodotSharp", hits);
    }

    private static string FindEraTdAssemblyPath()
    {
        // Test host runs from game/src/EraCore.Tests/bin/Debug/net8.0 — walk up
        // to the game/ root and into the presentation output. Godot.NET.Sdk
        // redirects build output to .godot/mono/temp/bin/Debug (no TFM
        // segment); a plain `dotnet build` configured with standard output
        // paths would land in bin/Debug/net8.0 — probe both. No workstation
        // path is hard-coded (RL9).
        var projectDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "presentation", "EraTD"));
        return new[]
        {
            Path.Combine(projectDir, ".godot", "mono", "temp", "bin", "Debug", "EraTD.dll"),
            Path.Combine(projectDir, "bin", "Debug", "net8.0", "EraTD.dll"),
        }.FirstOrDefault(File.Exists) ?? Path.Combine(
            projectDir, ".godot", "mono", "temp", "bin", "Debug", "EraTD.dll");
    }
}
