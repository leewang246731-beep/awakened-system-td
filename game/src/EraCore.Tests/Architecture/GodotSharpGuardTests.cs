using System.Reflection;
using EraCore;
using Xunit;

namespace EraCore.Tests.Architecture;

/// <summary>
/// D-01 architecture guard: EraCore must stay a pure C# logic library.
/// These tests fail the build the moment someone drags engine or banned
/// dependencies into the logic layer. Reflected off the compiled EraCore.dll.
/// </summary>
// [F-154] C# 热路径架构守卫（零 GodotSharp/禁反射发射/禁 Newtonsoft/禁直连网络）
public class GodotSharpGuardTests
{
    private static Assembly CoreAssembly => typeof(Guard).Assembly;

    [Fact]
    public void EraCore_references_no_GodotSharp()
    {
        var referenced = CoreAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(referenced, n => n.Contains("godot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EraCore_references_no_banned_assemblies()
    {
        // D-01/D-05: Newtonsoft.Json and direct System.Net.Http are forbidden;
        // System.Reflection.Emit* dies under NativeAOT (Web export prerequisite).
        var referenced = CoreAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(referenced, n => n.Contains("newtonsoft", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referenced, n => n.Equals("System.Net.Http", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, n => n.StartsWith("System.Reflection.Emit", StringComparison.Ordinal));
    }

    [Fact]
    public void EraCore_exports_no_Godot_namespace_types()
    {
        var offending = CoreAssembly.GetExportedTypes()
            .Where(t => t.Namespace?.StartsWith("Godot", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        Assert.Empty(offending);
    }
}
