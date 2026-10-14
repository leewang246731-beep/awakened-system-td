using EraCore.Agent.Governance;
using EraCore.Cinematics;
using EraCore.Progression;
using EraCore.Sim;
using EraCore.Tests.Sim;
using Xunit;
using static EraCore.Tests.Cinematics.VictoryCinematicsFixture;

namespace EraCore.Tests.Cinematics;

// [C-24] [F-002] [F-104] T-087 先定后演核心（RL12）：Decide 一次定死转盘
// 布局/落点/赠品（同 seed+判分输入→逐字节相同；人格/徽记只动点评）、
// 转盘无空档（每段一件白名单装饰奖励）、落点回推（WheelDynamics 纯函数：
// 指针终点角反解段=既定落点；尾部停顿帧数=脚本值）、光效分层（黄金恰比
// 白银多一层）、判分行三元组、非法入参拒绝。
public class VictorySettlementTests
{
    private static readonly IReadOnlyList<int> Profile = new[] { 2, 3, 4, 6, 9 };

    // ---------------------------------------------------------------- legal

    [Fact] // legal — 同输入双跑：既定演出逐记录、逐字节全等（RL12 基石）
    public void Identical_inputs_decide_byte_identical_performances()
    {
        DecidedVictoryPerformance first = Decided(ObjectiveTier.Gold);
        DecidedVictoryPerformance second = Decided(ObjectiveTier.Gold);

        Assert.Equal(first, second);
        Assert.Equal(VictorySettlementStore.Encode(first), VictorySettlementStore.Encode(second));
    }

    [Fact] // legal — Reseed 纪律：端口先被消耗过也不影响（决策流自带重播种）
    public void Decide_reseeds_the_port_regardless_of_prior_draws()
    {
        var rng = new SimRng(RunSeed);
        _ = rng.Next(0, 1000);
        _ = rng.NextDouble();

        DecidedVictoryPerformance decided = VictorySettlement.Decide(
            Settlement(ObjectiveTier.Gold), ChestTable(), rng, 6, RunSeed,
            SystemPersona.Advisor, TrustBadge.Trustworthy, TrustBadge.Trustworthy);

        Assert.Equal(Decided(ObjectiveTier.Gold), decided);
    }

    [Fact] // legal — 人格×徽记只调制点评：转盘/宝箱/判分行逐字节不动（F.4 措辞不能力）
    public void Persona_and_badge_change_only_the_commentary()
    {
        DecidedVictoryPerformance baseline = Decided(ObjectiveTier.Gold);

        foreach (SystemPersona persona in SystemPersonas.All)
        {
            DecidedVictoryPerformance decided = Decided(ObjectiveTier.Gold, persona: persona);
            Assert.Equal(baseline.Wheel, decided.Wheel);
            Assert.Equal(baseline.ChestItems, decided.ChestItems);
            Assert.Equal(baseline.VerdictRows, decided.VerdictRows);
            Assert.Equal(baseline.ChestLightLayers, decided.ChestLightLayers);
        }

        var lines = new HashSet<string>();
        foreach (SystemPersona persona in SystemPersonas.All)
        {
            lines.Add(Decided(ObjectiveTier.Gold, persona: persona).Commentary.MainLine);
        }

        Assert.Equal(SystemPersonas.All.Count, lines.Count); // 四人格四句点评（F-104）
    }

    [Theory] // legal — 四档：光效分层=青铜 1/白银 2/黄金 3/黄金+额外 3（黄金恰比白银多一层）
    [InlineData(ObjectiveTier.Bronze, 1)]
    [InlineData(ObjectiveTier.Silver, 2)]
    [InlineData(ObjectiveTier.Gold, 3)]
    [InlineData(ObjectiveTier.GoldExtra, 3)]
    public void Tiers_map_to_the_frozen_light_layers(ObjectiveTier tier, int expectedLayers)
    {
        DecidedVictoryPerformance decided = Decided(tier);

        Assert.Equal(expectedLayers, decided.ChestLightLayers);
        Assert.Equal(tier, decided.Tier);
        Assert.Equal(CampaignProgression.TierToChestId(tier), decided.ChestTierId);
        Assert.Equal((int)tier, decided.VerdictRows.Count(row => row.Passed));
    }

    [Fact] // legal — 判分行：三元组=生存/策略/挑战（生存恒第 1 元，SP-8c）
    public void Verdict_rows_are_the_three_objectives_in_frozen_order()
    {
        DecidedVictoryPerformance decided = Decided(ObjectiveTier.Gold);

        Assert.Equal(3, decided.VerdictRows.Count);
        Assert.Equal("生存 · 守住最终波", decided.VerdictRows[0].LabelZh);
        Assert.True(decided.VerdictRows[0].Passed);
        Assert.Equal(Pools().Strategy[0].NameZh, decided.VerdictRows[1].LabelZh);
        Assert.Equal(Pools().Challenge[0].NameZh, decided.VerdictRows[2].LabelZh);
    }

    [Fact] // legal — 转盘无空档：每段一件白名单装饰奖励；赠品=落点段（N8/SP-8c）
    public void Wheel_layout_has_no_blank_segments_and_grants_the_stopped_segment()
    {
        ChestTable table = ChestTable();
        DecidedVictoryPerformance decided = Decided(ObjectiveTier.GoldExtra, wheelSegments: 6);

        Assert.Equal(6, decided.Wheel.Segments.Count);
        foreach (VictoryWheelSegment segment in decided.Wheel.Segments)
        {
            Assert.Contains(segment.ItemId, table.ItemsByRarity[segment.Rarity]
                .Select(item => item.Id));
            Assert.Contains(segment.RewardClass, RewardWhitelist.Classes);
        }

        Assert.InRange(decided.Wheel.StopIndex, 0, decided.Wheel.Segments.Count - 1);
        VictoryWheelSegment stopped = decided.Wheel.Segments[decided.Wheel.StopIndex];
        Assert.Equal(stopped.ItemId, decided.Wheel.Granted.ItemId);
        Assert.Equal(stopped.Rarity, decided.Wheel.Granted.Rarity);
        Assert.Equal(stopped.RewardClass, decided.Wheel.Granted.RewardClass);
    }

    [Fact] // legal — 种子敏感性：换 run seed 换转盘流与宝箱流（同关卡同判分）
    public void A_different_run_seed_decides_a_different_wheel()
    {
        DecidedVictoryPerformance first = Decided(ObjectiveTier.Gold, runSeed: 110110);
        DecidedVictoryPerformance second = Decided(ObjectiveTier.Gold, runSeed: 220220);

        Assert.NotEqual(first.Wheel, second.Wheel);
        Assert.NotEqual(first.ChestItems, second.ChestItems);
    }

    [Fact] // legal — 种子公式冻结：run seed+关卡 id+完成数 → 手工重放同流同落点（抽序冻结）
    public void The_wheel_seed_formula_and_draw_order_are_frozen()
    {
        DecidedVictoryPerformance decided = Decided(ObjectiveTier.Gold);
        ChestTable table = ChestTable();
        var rng = new SimRng(RunSeed);
        rng.Reseed(CampaignHash.Combine(
            "campaign:level-e1-01" + VictorySettlement.WheelSeedSuffix, RunSeed ^ 2));

        int segments = 6;
        for (int i = 0; i < segments; i++)
        {
            _ = rng.NextDouble(); // 段稀有度（与实现同加权走查，只推进流）
            _ = rng.Next(0, table.ItemsByRarity[table.OrderedWeights[0].Rarity].Count); // 段物品
        }

        int expectedStop = rng.Next(0, segments);
        Assert.Equal(expectedStop, decided.Wheel.StopIndex);
        Assert.Equal(decided.Wheel.Segments.Count, segments);
    }

    [Fact] // legal — 关卡入种：同 seed 同判分，换关卡换转盘流
    public void A_different_level_decides_a_different_wheel()
    {
        DecidedVictoryPerformance first = Decided(ObjectiveTier.Gold, levelId: "campaign:level-e1-01");
        DecidedVictoryPerformance second = Decided(ObjectiveTier.Gold, levelId: "campaign:level-e1-02");

        Assert.NotEqual(first.Wheel, second.Wheel);
    }

    // ------------------------------------------------- 转盘动力学（演出倒推）

    [Theory] // legal — 落点回推（RL12 断言）：指针终点角反解段==既定落点，逐落点穷举
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void The_pointer_lands_exactly_on_the_decided_segment(int stopIndex)
    {
        double finalAngle = WheelDynamics.PointerAngle(6, stopIndex, 150, Profile, 150);

        Assert.Equal(stopIndex, WheelDynamics.SegmentAt(finalAngle, 6));
    }

    [Fact] // legal — 尾部停顿帧数脚本化：每组停顿窗口内指针不动，组首帧跳变，窗口长=脚本帧数
    public void The_tail_dwells_for_the_scripted_pause_frames()
    {
        const int duration = 150;
        int headTicks = duration - Profile.Sum(); // 126：快转段
        var angles = new List<double>();
        for (int elapsed = 0; elapsed <= duration; elapsed++)
        {
            angles.Add(WheelDynamics.PointerAngle(6, 2, duration, Profile, elapsed));
        }

        // 头段每拍都在走（快转不停）
        for (int tick = 1; tick <= headTicks; tick++)
        {
            Assert.NotEqual(angles[tick - 1], angles[tick]);
        }

        // 尾段：组首帧跳变一次，组内停顿帧数=脚本值（帧帧不动）
        int windowStart = headTicks + 1;
        foreach (int pause in Profile)
        {
            Assert.NotEqual(angles[windowStart - 1], angles[windowStart]); // 组首帧跳变
            for (int frame = 0; frame < pause; frame++)
            {
                Assert.Equal(angles[windowStart], angles[windowStart + frame]); // 停顿：帧不动
            }

            windowStart += pause;
        }

        Assert.Equal(duration, windowStart - 1); // 尾段窗口恰好铺满到停拍
        for (int i = 1; i < Profile.Count; i++)
        {
            Assert.True(Profile[i] >= Profile[i - 1]); // 停顿非递减=减速读感（脚本校验同口径）
        }
    }

    [Fact] // legal — 越界拍数钳制：到期后指针不再移动（回看/长按不受额外拍影响）
    public void Elapsed_beyond_the_duration_is_clamped()
    {
        double atExpiry = WheelDynamics.PointerAngle(6, 3, 150, Profile, 150);
        double beyond = WheelDynamics.PointerAngle(6, 3, 150, Profile, 150 + 60);
        double beforeStart = WheelDynamics.PointerAngle(6, 3, 150, Profile, -5);

        Assert.Equal(atExpiry, beyond);
        Assert.Equal(WheelDynamics.PointerAngle(6, 3, 150, Profile, 0), beforeStart);
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — null 入参：逐项拒绝
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => VictorySettlement.Decide(
            null!, ChestTable(), new SimRng(RunSeed), 6, RunSeed,
            SystemPersona.Advisor, TrustBadge.Trustworthy, TrustBadge.Trustworthy));
        Assert.Throws<ArgumentNullException>(() => VictorySettlement.Decide(
            Settlement(ObjectiveTier.Gold), null!, new SimRng(RunSeed), 6, RunSeed,
            SystemPersona.Advisor, TrustBadge.Trustworthy, TrustBadge.Trustworthy));
        Assert.Throws<ArgumentNullException>(() => VictorySettlement.Decide(
            Settlement(ObjectiveTier.Gold), ChestTable(), null!, 6, RunSeed,
            SystemPersona.Advisor, TrustBadge.Trustworthy, TrustBadge.Trustworthy));
        Assert.Throws<ArgumentNullException>(() => VictorySettlement.VerdictRowsOf(null!));
    }

    [Theory] // illegal — 转盘段数下限（boundary：3 段合法，2 段拒绝）
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Wheel_segment_floor_is_enforced(int segments, bool legal)
    {
        if (legal)
        {
            DecidedVictoryPerformance decided = Decided(ObjectiveTier.Gold, wheelSegments: segments);
            Assert.Equal(segments, decided.Wheel.Segments.Count);
        }
        else
        {
            Assert.Throws<ArgumentException>(
                () => Decided(ObjectiveTier.Gold, wheelSegments: segments));
        }
    }

    [Fact] // illegal — 空关卡 id 拒绝（种子域需要关卡 id）
    public void A_blank_level_id_is_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => VictorySettlement.Decide(
                Settlement(ObjectiveTier.Gold, levelId: "  "), ChestTable(), new SimRng(RunSeed), 6, RunSeed,
                SystemPersona.Advisor, TrustBadge.Trustworthy, TrustBadge.Trustworthy));
    }

    [Fact] // illegal — 动力学：空停顿表/零帧停顿拒绝
    public void Malformed_decel_profiles_are_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => WheelDynamics.PointerAngle(6, 1, 150, Array.Empty<int>(), 10));
        Assert.Throws<ArgumentException>(
            () => WheelDynamics.PointerAngle(6, 1, 150, new[] { 4, 0 }, 10));
    }

    [Fact] // illegal — 段反解：segmentCount<1 拒绝
    public void Segment_lookup_needs_a_positive_segment_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WheelDynamics.SegmentAt(90.0, 0));
    }

    [Fact] // illegal — 未知档位光效层数：拒绝（穷举 switch 无默认吸收）
    public void An_unknown_tier_has_no_light_layer_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VictorySettlement.LightLayers((ObjectiveTier)99));
    }
}
