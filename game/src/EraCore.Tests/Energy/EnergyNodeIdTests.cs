using EraCore.Energy;
using Xunit;

namespace EraCore.Tests.Energy;

// [C-08] EnergyNodeId — opaque ledger identity primitive (T-033).
public class EnergyNodeIdTests
{
    [Fact] // legal — value equality, the ledger keys nodes by it
    public void Same_value_ids_are_equal_and_hash_equal()
    {
        var a = new EnergyNodeId(7);
        var b = new EnergyNodeId(7);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a == b);
    }

    [Fact] // legal — distinct values are distinct ids
    public void Different_value_ids_are_unequal()
    {
        Assert.NotEqual(new EnergyNodeId(1), new EnergyNodeId(2));
    }

    // batch2 审查 F3：原 Value_round_trips / Works_as_dictionary_key 两测为纯
    // record 形状断言（语言保证复述，恒真风险）——替换为有意义的构造边界：
    // 非法负值构造行为 = 原语层不拒绝（opaque，域警察在签发方/账本侧）。
    // （未走 ci-allow: T3 豁免路线：类级 [C-08] 标签已使全部方法计入 T3 关联，
    // 扫描器的豁免机制只能压告警、无法把它们移出计数分母——替换为边界测合规。）
    [Fact] // boundary — the primitive is deliberately unvalidated: negative and extreme values construct legally and stay distinct (policing the id domain is the issuer's job, not the record's)
    public void Negative_and_extreme_values_construct_legally_and_stay_distinct()
    {
        var negative = new EnergyNodeId(-1);
        var zero = new EnergyNodeId(0);
        var min = new EnergyNodeId(long.MinValue);

        Assert.Equal(-1L, negative.Value);            // 非法负值：构造不抛、原样保留（opaque 契约）
        Assert.Equal(long.MinValue, min.Value);       // 极值往返：无钳制、无归一
        Assert.NotEqual(negative, zero);              // 负值与 0 是不同节点
        Assert.NotEqual(min, negative);               // 极值与普通负值是不同节点
    }

    [Fact] // boundary — mixed-sign ids key one dictionary independently (producers, consumers and virtual taps share a single ledger namespace)
    public void Mixed_sign_ids_key_one_dictionary_independently()
    {
        var map = new System.Collections.Generic.Dictionary<EnergyNodeId, string>
        {
            [new EnergyNodeId(-1)] = "virtual-pool-tap",
            [new EnergyNodeId(0)] = "labor-camp",
            [new EnergyNodeId(1)] = "tower",
            [new EnergyNodeId(long.MinValue)] = "edge",
        };

        Assert.Equal(4, map.Count);
        Assert.Equal("virtual-pool-tap", map[new EnergyNodeId(-1)]);
        Assert.Equal("edge", map[new EnergyNodeId(long.MinValue)]);
    }
}
