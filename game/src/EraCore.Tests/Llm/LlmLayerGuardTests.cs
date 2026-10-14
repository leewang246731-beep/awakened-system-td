using EraCore.Llm;
using EraCore.Tests.Architecture;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// C-16 层架构守卫（T-072 增设）：EraCore 程序集仍零 System.Net.Http/
/// Newtonsoft/Reflection.Emit 引用；EraCore 内零 IHttpPort 实现（真实网络
/// 只存在于测试程序集——CS-1.3）。
/// </summary>
// [F-154][F-162] C-16 T-072：网络实现与解析分离的守卫
public class LlmLayerGuardTests
{
    private static string CoreAssemblyPath => typeof(LlmGateway).Assembly.Location;

    [Fact]
    public void EraCore_llm_layer_references_no_banned_assemblies()
    {
        // legal: 守卫复用（GodotSharpGuardTests 同一引擎）
        var offending = AssemblyGuard.FindForbiddenReferences(
            CoreAssemblyPath,
            new[] { "newtonsoft", "System.Net.Http", "System.Reflection.Emit" });

        Assert.Empty(offending);
    }

    [Fact]
    public void EraCore_contains_no_IHttpPort_implementation()
    {
        // illegal: EraCore 内出现端口实现（真实网络必须留在测试程序集/表现层）
        var implementers = typeof(IHttpPort).Assembly.GetTypes()
            .Where(t => typeof(IHttpPort).IsAssignableFrom(t) && t != typeof(IHttpPort))
            .ToList();

        Assert.Empty(implementers);
    }

    [Fact]
    public void Gateway_is_the_only_ILlmTransport_implementation_in_EraCore()
    {
        // legal: K-9 端口实现点唯一且可定位
        var implementers = typeof(EraCore.Contracts.Agent.ILlmTransport).Assembly.GetTypes()
            .Where(t => typeof(EraCore.Contracts.Agent.ILlmTransport).IsAssignableFrom(t)
                && t.IsClass && !t.IsAbstract)
            .ToList();

        var implementer = Assert.Single(implementers);
        Assert.Equal(typeof(LlmGateway), implementer);
    }
}
