using System.Reflection;
using EraCore;
using EraCore.Net;
using Xunit;

namespace EraCore.Tests.Architecture;

/// <summary>
/// T-131 single-player zero-coupling guard (F-161 acceptance: 「单机路径
/// 不因预留增加耦合」): reflects over the compiled EraCore assembly and
/// asserts that NO type outside <c>EraCore.Net</c> references any
/// <c>EraCore.Net</c> type through its visible surface — base types,
/// implemented interfaces, fields, properties, events and method
/// signatures, generic arguments included. The deterministic core must stay
/// unaware of the reservation; dependencies point one way (Net → Sim).
/// <para>
/// Non-vacuity controls: the same scanner pointed at <c>EraCore.Sim</c>
/// MUST find references (Checkpoint/Agent reference Sim heavily — the
/// scanner demonstrably detects real coupling), and the Net types
/// themselves MUST reference <c>EraCore.Sim</c> (the dependency direction
/// the reservation is allowed to use).
/// </para>
/// </summary>
// [F-161] T-131 单机路径零耦合守卫（预留面验收）：三个用例分别为零引用主断言/
// Sim 对照组（扫描器非空转证明）/Net→Sim 依赖方向断言。
public class NetCouplingGuardTests
{
    private const string ReservedNamespace = "EraCore.Net";
    private const string CoreNamespace = "EraCore.Sim";

    // [F-161] 单机路径零耦合：非 Net 类型不引用任何 EraCore.Net 类型
    [Fact]
    public void No_type_outside_Net_references_the_reserved_Net_types()
    {
        var offenders = new List<string>();

        foreach (Type type in typeof(Guard).Assembly.GetExportedTypes())
        {
            if (IsInNamespace(type, ReservedNamespace))
            {
                continue;
            }

            foreach (Type referenced in ReferencedTypes(type))
            {
                if (IsInNamespace(referenced, ReservedNamespace))
                {
                    offenders.Add($"{type.FullName} → {referenced.FullName}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    // [F-161] 负向对照（守卫非空转）：同一扫描器指向 EraCore.Sim 必须找到引用
    [Fact]
    public void Guard_detects_references_when_they_exist_Sim_control()
    {
        var hits = new List<string>();

        foreach (Type type in typeof(Guard).Assembly.GetExportedTypes())
        {
            if (IsInNamespace(type, CoreNamespace))
            {
                continue;
            }

            foreach (Type referenced in ReferencedTypes(type))
            {
                if (IsInNamespace(referenced, CoreNamespace))
                {
                    hits.Add($"{type.FullName} → {referenced.FullName}");
                }
            }
        }

        Assert.Contains(hits, h => h.Contains("EraCore.Sim.Simulation"));          // Checkpoint/Replay 的重建消费面
        Assert.Contains(hits, h => h.Contains("EraCore.Sim.SimulationConfig"));    // 关卡装配消费面（多消费方）
        Assert.True(hits.Count >= 3, $"expected several Sim consumers, got {hits.Count}");
    }

    // [F-161] 方向对照：预留面自身引用 EraCore.Sim（依赖单向 Net→Sim 合法）
    [Fact]
    public void Net_types_reference_Sim_in_the_allowed_direction()
    {
        var netTypes = typeof(Guard).Assembly.GetExportedTypes()
            .Where(t => IsInNamespace(t, ReservedNamespace))
            .ToList();

        Assert.Equal(
            new[]
            {
                "EraCore.Net.ICommandTransport",
                "EraCore.Net.ISnapshotChannel",
                "EraCore.Net.NetLinkStateMachine",
                "EraCore.Net.NetLinkStatus",
            },
            netTypes.Select(t => t.FullName!).OrderBy(n => n, StringComparer.Ordinal));

        var referenced = netTypes.SelectMany(ReferencedTypes).ToList();
        Assert.Contains(referenced, t => t.FullName == "EraCore.Contracts.Sim.SimCommand");
        Assert.Contains(referenced, t => t.FullName == "EraCore.Sim.SimHashSample");
    }

    /// <summary>Namespace containment (exact segment, not prefix-of-longer).</summary>
    private static bool IsInNamespace(Type type, string ns) =>
        type.Namespace == ns || (type.Namespace?.StartsWith(ns + ".", StringComparison.Ordinal) ?? false);

    /// <summary>
    /// Every type visible in one type's member surface: base chain,
    /// interfaces, declared fields/properties/events (public and private),
    /// method signatures (return + parameters). Generic arguments and array
    /// elements are unwrapped recursively.
    /// </summary>
    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();

        void Enqueue(Type? candidate)
        {
            while (candidate is not null)
            {
                if (candidate.HasElementType)
                {
                    // Array/pointer/byref: the element type is the reference.
                    Enqueue(candidate.GetElementType());
                    return;
                }

                if (candidate.IsGenericParameter)
                {
                    // Open-constructed signature: constraints may carry real types.
                    foreach (Type constraint in candidate.GetGenericParameterConstraints())
                    {
                        queue.Enqueue(constraint);
                    }

                    return;
                }

                if (seen.Add(candidate))
                {
                    queue.Enqueue(candidate);
                    foreach (Type argument in candidate.GetGenericArguments())
                    {
                        Enqueue(argument);
                    }
                }

                return;
            }
        }

        for (Type? current = type.BaseType; current is not null; current = current.BaseType)
        {
            Enqueue(current);
        }

        foreach (Type implemented in type.GetInterfaces())
        {
            Enqueue(implemented);
        }

        const BindingFlags AllDeclared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (FieldInfo field in type.GetFields(AllDeclared))
        {
            Enqueue(field.FieldType);
        }

        foreach (PropertyInfo property in type.GetProperties(AllDeclared))
        {
            Enqueue(property.PropertyType);
        }

        foreach (EventInfo eventInfo in type.GetEvents(AllDeclared))
        {
            Enqueue(eventInfo.EventHandlerType);
        }

        foreach (MethodInfo method in type.GetMethods(AllDeclared))
        {
            Enqueue(method.ReturnType);
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Enqueue(parameter.ParameterType);
            }
        }

        while (queue.Count > 0)
        {
            yield return queue.Dequeue();
        }
    }
}
