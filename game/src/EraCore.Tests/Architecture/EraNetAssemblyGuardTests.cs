using EraCore;
using EraNet;
using Xunit;

namespace EraCore.Tests.Architecture;

/// <summary>
/// T-139 assembly-direction guard: the new EraNet implementation assembly is
/// a LEAF above the deterministic core — it may reference EraCore, and the
/// core must never reference it back (the single-player path stays untouched
/// by construction: nothing outside EraNet references EraNet types, and
/// EraCore's own NetCouplingGuardTests already pin that no EraCore type
/// touches EraCore.Net). D-01's banned-dependency list applies to EraNet
/// exactly as to EraCore: no Godot, no direct networking stack, no
/// Newtonsoft, no Reflection.Emit — the loopback must stay a pure logic
/// fabric (real sockets live in the presentation layer only).
/// </summary>
// [F-161] T-139 程序集方向守卫：EraNet→EraCore 单向+D-01 禁依赖清单扩至 EraNet。
public class EraNetAssemblyGuardTests
{
    private static string EraNetAssemblyPath => typeof(NetGameHost).Assembly.Location;
    private static string EraCoreAssemblyPath => typeof(Guard).Assembly.Location;

    [Fact] // boundary — EraCore 永不反向引用 EraNet（单机路径构造上够不着联机面）
    public void EraCore_references_no_EraNet()
    {
        Assert.Empty(AssemblyGuard.FindForbiddenReferences(
            EraCoreAssemblyPath, new[] { "eranet" }));
    }

    [Fact] // boundary — EraNet 引用 EraCore（方向非空转证明：叶子必须真的连在核心上）
    public void EraNet_references_EraCore_in_the_allowed_direction()
    {
        Assert.Contains(
            AssemblyGuard.ReferenceNames(EraNetAssemblyPath),
            name => name.Equals("EraCore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // boundary — D-01 禁依赖清单对 EraNet 同律：零 Godot/零直连网络栈/零 Newtonsoft/零 Emit
    public void EraNet_references_no_banned_assemblies()
    {
        Assert.Empty(AssemblyGuard.FindForbiddenReferences(
            EraNetAssemblyPath,
            new[] { "godot", "system.net", "newtonsoft", "System.Reflection.Emit" }));
    }
}
