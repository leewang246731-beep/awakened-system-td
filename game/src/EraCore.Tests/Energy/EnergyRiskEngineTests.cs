using EraCore.Energy;
using Xunit;

namespace EraCore.Tests.Energy;

// [C-08][F-010] EnergyRiskEngine (T-113, SP-4 能源风险阈值表): 表驱动触发
// （满载窗口边界 N−1/N）、后果数值手算对照、玩家对策可避免、确定性与参数
// 非法拒绝。全部阈值/后果数值为 11-spec-tables SP-4 L146/L147 [NEW-原型校准]
// 值，经夹具表注入（era3.json 数据侧归 T-112——本测试不读内容文件）。

/// <summary>
/// SP-4 表值夹具（测试侧锚定，逐字来自 11-spec-tables SP-4 L146/L147；
/// 生产数值走 data/eras/{era}.json → EnergyRiskTuning，引擎零硬编码 RL7）。
/// </summary>
internal static class Sp4RiskTables
{
    /// <summary>SP-4 L146 E3 过热爆炸行（触发/热量/半径/后果全列）。</summary>
    public static readonly OverheatTuning Overheat = new(
        OverloadLoadRatio: 1.00,          // 负载率 ≥100%
        OverloadSustainedSeconds: 30.0,   // 连续 30 s
        HeatCapacity: 100.0,              // 热量槽达 100
        BaseHeatPerSecond: 2.0,           // 过热期 +2/s
        HeatLoadStepRatio: 0.10,          // 负载率每 +10%
        HeatPerLoadStepPerSecond: 1.0,    // 追加 +1/s
        HeatCooldownPerSecond: 5.0,       // 卸载后 −5/s 回落
        ExplosionRadiusCells: 4.0,        // 爆炸半径 4 格
        BuildingDamageMaxHpFraction: 0.50, // 建筑 最大 HP×50%
        EnemyDamage: 300.0,               // 对敌 300 伤害
        GeneratorDowntimeSeconds: 60.0,   // 停机 60 s
        GeneratorDerateFraction: 0.50,    // 50% 功率
        GeneratorDerateSeconds: 30.0);    // 运行 30 s

    /// <summary>SP-4 L147 E4 熔毁行（触发/倒计时/中断/半径/后果全列）。</summary>
    public static readonly MeltdownTuning Meltdown = new(
        OverloadLoadRatio: 1.20,           // 负载率 ≥120% 连续 20 s
        OverloadSustainedSeconds: 20.0,
        InstantLoadRatio: 1.50,            // 或瞬时 ≥150%
        CountdownSeconds: 45.0,            // 熔毁倒计时 45 s
        InterruptUnloadFraction: 0.30,     // 卸载 ≥30% 负载可中断
        RequireCoolant: true,              // 任务卡「断冷→熔毁」通道
        ExplosionRadiusCells: 6.0,         // 半径 6 格
        BuildingDamageMaxHpFraction: 0.80, // 半径内建筑 最大 HP×80%
        RadiationZoneSeconds: 60.0,        // 地面污染 60 s
        RadiationEnemySlowFraction: 0.25,  // 过境敌 −25% 速度
        WreckRecoveryCostFraction: 0.30);  // 残骸修复口径 原成本×30%

    /// <summary>双通道完整表（E3+E4）。</summary>
    public static EnergyRiskTuning Full() => new(Overheat, Meltdown);

    /// <summary>
    /// 仅 E3 通道的表（E4=null——E3 蒸汽代无核电；亦作 E4 留接口的反向证明：
    /// 同一框架少一行数据即关闭该通道）。
    /// </summary>
    public static EnergyRiskTuning OverheatOnly() => new(Overheat, Meltdown: null);
}

// [C-08][F-010] EnergyRiskEngine 单元表驱动（T-113）：下列方法覆盖表值触发边界、
// 公式手算、后果数值、对策可避免、确定性与非法参数（类级关联，CS-3.2）。
public class EnergyRiskEngineTests
{
    private static readonly EnergyNodeId Culprit = new(7001);

    /// <summary>推进 n 个逐 tick 结算（tick 从 1 计），返回期间全部事件。</summary>
    private static List<EnergyRiskOutcome> Run(
        EnergyRiskEngine engine,
        long fromTick,
        long ticks,
        double loadRatio,
        bool coolantSupplied = true,
        EnergyNodeId? culprit = null)
    {
        var events = new List<EnergyRiskOutcome>();
        for (long i = 0; i < ticks; i++)
        {
            events.AddRange(engine.OnSettle(fromTick + i, dtTicks: 1, loadRatio, coolantSupplied, culprit ?? Culprit));
        }

        return events;
    }

    // ---- 表驱动触发：负载率 → 爆炸 tick（手算对照） --------------------------
    // 手算（SP-4 L146 公式 + 30 Hz tick 域，首个过载结算=tick 1）：
    //   热量速率 = 2 + (load−1.00)/0.10 × 1  [点/s]
    //   热量满 tick = ⌈100 / 速率 × 30⌉；窗口满 tick = 900；
    //   爆炸 tick = max(两者) —— 两门在 1.00 档咬热量门、在重载档咬窗口门。
    [Theory] // legal + boundary（N−1 行为 vs N 行为逐档对照）
    [InlineData(1.00, 1500)] // 热量 2/s → 0→100 需 50 s=1500 tick（SP-4「≥50 s 连续满载最快」边界）；窗口 900 被包含
    [InlineData(1.10, 1000)] // 热量 3/s → 1000 tick 满；窗口 900 被包含
    [InlineData(1.20, 900)]  // 热量 4/s → 750 tick 已满，等窗口 900（窗口门咬合）
    [InlineData(1.50, 900)]  // 热量 7/s → 429 tick 已满，等窗口 900
    public void Overheat_fires_exactly_at_table_tick(double loadRatio, long expectedTick)
    {
        var engine = new EnergyRiskEngine(Sp4RiskTables.OverheatOnly());

        // N−1：前一 tick 仍未触发
        List<EnergyRiskOutcome> before = Run(engine, 1, expectedTick - 1, loadRatio);
        Assert.Empty(before);

        // N：恰在表值 tick 触发一次
        List<EnergyRiskOutcome> at = Run(engine, expectedTick, 1, loadRatio);
        EnergyRiskOutcome outcome = Assert.Single(at);
        Assert.Equal(expectedTick, outcome.Tick);
        Assert.Equal(EnergyRiskEventKind.OverheatExplosion, outcome.Kind);
        Assert.Equal(Culprit, outcome.Culprit);
        Assert.Equal(1, engine.OverheatFireCount);
    }

    [Theory] // boundary —— 阈值含等号：恰在 100% 计入过载，恰在 99.99% 不计
    [InlineData(1.00, true)]
    [InlineData(0.9999, false)]
    public void Overload_threshold_is_inclusive_at_100_percent(double loadRatio, bool accumulates)
    {
        var engine = new EnergyRiskEngine(Sp4RiskTables.OverheatOnly());

        _ = Run(engine, 1, 30, loadRatio);

        Assert.Equal(accumulates ? 30 : 0, engine.OverloadSustainedTicks);
        Assert.Equal(accumulates ? 2.0 : 0.0, engine.Heat, 9); // 2/s × 1 s 手算
    }

    [Theory] // legal —— 热量公式手算：base 2 + 每 +10% 追加 1（SP-4 L146）
    [InlineData(1.00, 2.0)]
    [InlineData(1.10, 3.0)]
    [InlineData(1.20, 4.0)]
    [InlineData(1.30, 5.0)]
    [InlineData(1.50, 7.0)]
    [InlineData(0.80, 2.0)] // 阈值下不叠加（margin 取 0——公式纯函数语义）
    public void Heat_gain_formula_matches_SP4_hand_computation(double loadRatio, double expectedPerSecond)
    {
        var engine = new EnergyRiskEngine(Sp4RiskTables.OverheatOnly());

        Assert.Equal(expectedPerSecond, engine.HeatGainPerSecond(loadRatio), 9);
    }

    [Fact] // legal —— 卸载后 −5/s 回落，手算：2 点热量经 12 tick（0.4 s）归零
    public void Heat_decays_at_cooldown_rate_after_unload()
    {
        var engine = new EnergyRiskEngine(Sp4RiskTables.OverheatOnly());

        _ = Run(engine, 1, 30, 1.00);   // 热量 = 2/s × 1 s = 2
        Assert.Equal(2.0, engine.Heat, 9);

        _ = Run(engine, 31, 11, 0.99);  // 11 tick = 0.3667 s → 2 − 5×0.3667 = 0.1667
        Assert.Equal(2.0 - 5.0 * (11.0 / 30.0), engine.Heat, 9);

        _ = Run(engine, 42, 1, 0.99);   // 12 tick 累计 0.4 s → 2 − 2 = 0（钳到 0）
        Assert.Equal(0.0, engine.Heat, 9);
    }

    [Fact] // boundary —— 卸载即中断：窗口清零（SP-4 L146「期间卸载即中断」）
    public void Unload_resets_the_sustained_window_immediately()
    {
        var engine = new EnergyRiskEngine(Sp4RiskTables.OverheatOnly());

        _ = Run(engine, 1, 899, 1.20); // 窗口 899/900、热量已满——差 1 tick
        Assert.Empty(engine.OnSettle(900, 1, 0.50, true, Culprit));
        Assert.Equal(0, engine.OverloadSustainedTicks);

        // 重新满载从零起算：再 900 tick（窗口门）后爆炸，落在 900+900=1800
        List<EnergyRiskOutcome> events = Run(engine, 901, 900, 1.20);
        EnergyRiskOutcome outcome = Assert.Single(events);
        Assert.Equal(1800, outcome.Tick);
    }

    [Fact] // legal —— 对策可避免（防"必然爆炸"）：不满载永不触发
    public void Counterplay_staying_below_overload_never_explodes()
    {
        var engine = new EnergyRiskEngine(Sp4RiskTables.OverheatOnly());

        Assert.Empty(Run(engine, 1, 20_000, 0.99));
        Assert.Equal(0, engine.OverheatFireCount);

        // 间歇满载同样安全：每段满载未过任一门即卸载中断（窗口清零+热量回落）
        for (int cycle = 0; cycle < 20; cycle++)
        {
            long tick = 1 + cycle * 1000L;
            Assert.Empty(Run(engine, tick, 500, 1.00));       // 热量 +33.3、窗口 500
            Assert.Empty(Run(engine, tick + 500, 500, 0.50)); // 卸载：窗口清零、热量回落
        }

        Assert.Equal(0, engine.OverheatFireCount);
        Assert.Equal(0.0, engine.Heat, 9);
    }

    // ---- E4 熔毁：表驱动触发与中断 ------------------------------------------

    [Fact] // legal + boundary —— ≥120% 连续 20 s 起倒、45 s 后熔毁：599/600 与 1949/1950
    public void Meltdown_sustained_trigger_boundaries()
    {
        var engine = new EnergyRiskEngine(Sp4RiskTables.Full());

        _ = Run(engine, 1, 599, 1.20); // 19.97 s：未到 20 s 门
        Assert.Null(engine.MeltdownCountdownTicksRemaining);

        _ = Run(engine, 600, 1, 1.20); // 恰 20 s：倒计时武装（45 s=1350 tick）
        Assert.Equal(1350, engine.MeltdownCountdownTicksRemaining);
        Assert.Equal(1.20, engine.MeltdownCountdownStartLoad, 9);

        List<EnergyRiskOutcome> before = Run(engine, 601, 1349, 1.20); // 到 tick 1949
        Assert.DoesNotContain(before, o => o.Kind == EnergyRiskEventKind.Meltdown);

        List<EnergyRiskOutcome> at = Run(engine, 1950, 1, 1.20); // 恰倒计归零
        EnergyRiskOutcome meltdown = Assert.Single(at, o => o.Kind == EnergyRiskEventKind.Meltdown);
        Assert.Equal(1950, meltdown.Tick);
        Assert.Equal(1, engine.MeltdownFireCount);
    }

    [Fact] // boundary —— 1.19% 档永不武装熔毁通道（120% 含等号的另一侧）
    public void Load_below_meltdown_threshold_never_arms_countdown()
    {
        var engine = new EnergyRiskEngine(Sp4RiskTables.Full());

        Assert.DoesNotContain(Run(engine, 1, 10_000, 1.19), o => o.Kind == EnergyRiskEventKind.Meltdown);
        Assert.Null(engine.MeltdownCountdownTicksRemaining);
    }

    [Fact] // legal + boundary —— 瞬时 ≥150% 即起倒（1.50 含等号；1.4999 走持续门）
    public void Meltdown_instant_spike_starts_countdown_immediately()
    {
        var instant = new EnergyRiskEngine(Sp4RiskTables.Full());

        // 手算：tick 1 起倒（剩余 1350），tick 2..1351 逐 tick 递减，归零于
        // tick 1351（= 起倒 tick + 45 s×30 —— 倒计时不吃起倒当 tick）
        Assert.DoesNotContain(Run(instant, 1, 1350, 1.50), o => o.Kind == EnergyRiskEventKind.Meltdown);
        List<EnergyRiskOutcome> at = Run(instant, 1351, 1, 1.50);
        Assert.Contains(at, o => o.Kind == EnergyRiskEventKind.Meltdown && o.Tick == 1351);
        Assert.Equal(1.50, instant.MeltdownCountdownStartLoad, 9);

        var subSpike = new EnergyRiskEngine(Sp4RiskTables.Full());
        _ = Run(subSpike, 1, 10, 1.4999); // 瞬时门差一丝 → 只能走 20 s 持续门
        Assert.Null(subSpike.MeltdownCountdownTicksRemaining);
    }

    [Theory] // boundary —— 卸载 ≥30% 可中断：1.05（恰 30% 削减）中断 / 1.0501 不中断
    [InlineData(1.0500, true)]
    [InlineData(1.0501, false)]
    public void Meltdown_interrupt_needs_thirty_percent_unload(double reliefLoad, bool interrupts)
    {
        var engine = new EnergyRiskEngine(Sp4RiskTables.Full());

        _ = Run(engine, 1, 1, 1.50);     // 瞬时 150% 起倒：startLoad=1.50
        _ = Run(engine, 2, 999, 1.50);   // 倒计时进行到 tick 1000（剩 351）
        Assert.Equal(351, engine.MeltdownCountdownTicksRemaining);

        _ = Run(engine, 1001, 1, reliefLoad); // 中断判定（相对削减 ≥30%：1.5×0.7=1.05）
        if (interrupts)
        {
            Assert.Null(engine.MeltdownCountdownTicksRemaining);

            // 对策验证：卸载后维持低载，永不熔毁
            Assert.DoesNotContain(
                Run(engine, 1002, 5000, reliefLoad),
                o => o.Kind == EnergyRiskEventKind.Meltdown);
            Assert.Equal(0, engine.MeltdownFireCount);
        }
        else
        {
            Assert.NotNull(engine.MeltdownCountdownTicksRemaining);
            Assert.Contains(Run(engine, 1002, 400, 1.50),
                o => o.Kind == EnergyRiskEventKind.Meltdown && o.Tick == 1351);
        }
    }

    [Fact] // legal —— 断冷起倒、恢复冷却液中断（任务卡「断冷→熔毁」+对策「冷却液供给可避免」）
    public void Coolant_loss_arms_countdown_and_restore_interrupts()
    {
        var engine = new EnergyRiskEngine(Sp4RiskTables.Full());

        _ = Run(engine, 1, 100, 0.50, coolantSupplied: true); // 低载+供冷：无倒计时
        Assert.Null(engine.MeltdownCountdownTicksRemaining);

        _ = Run(engine, 101, 1, 0.50, coolantSupplied: false); // 断冷 → 起倒
        Assert.Equal(1350, engine.MeltdownCountdownTicksRemaining);

        _ = Run(engine, 102, 99, 0.50, coolantSupplied: false);
        _ = Run(engine, 201, 1, 0.50, coolantSupplied: true); // 恢复冷却 → 中断
        Assert.Null(engine.MeltdownCountdownTicksRemaining);

        _ = Run(engine, 202, 98, 0.50, coolantSupplied: true);
        _ = Run(engine, 300, 1, 0.50, coolantSupplied: false); // 再断冷且不复原
        List<EnergyRiskOutcome> events = Run(engine, 301, 1350, 0.50, coolantSupplied: false);

        // 手算：第二次断冷起倒于 tick 300，45 s=1350 tick 后于 tick 1650 熔毁
        Assert.Contains(events, o => o.Kind == EnergyRiskEventKind.Meltdown && o.Tick == 1650);
        Assert.Equal(1, engine.MeltdownFireCount);
    }

    [Fact] // legal —— RequireCoolant=false：断冷不是该代触发通道（E3 蒸汽代语义）
    public void Coolant_is_ignored_when_the_row_does_not_require_it()
    {
        var engine = new EnergyRiskEngine(new EnergyRiskTuning(
            Sp4RiskTables.Overheat,
            Sp4RiskTables.Meltdown with { RequireCoolant = false }));

        Assert.Empty(Run(engine, 1, 10_000, 0.50, coolantSupplied: false));
        Assert.Equal(0, engine.MeltdownFireCount);
    }

    // ---- 后果数值断言（SP-4 后果列逐项手算对照） ------------------------------

    [Fact] // legal —— E3 后果列：半径 4/建筑 50% maxHP/敌 300/停机 60 s/50% 功率 30 s
    public void Overheat_outcome_carries_SP4_consequence_numbers()
    {
        var engine = new EnergyRiskEngine(Sp4RiskTables.OverheatOnly());

        EnergyRiskOutcome outcome = Assert.Single(Run(engine, 1, 1500, 1.00));

        Assert.Equal(4.0, outcome.RadiusCells);
        Assert.Equal(0.50, outcome.BuildingDamageMaxHpFraction); // 手算：200 HP 建筑受 100 点
        Assert.Equal(300.0, outcome.EnemyDamage);                // SP-4「对敌：300 伤害」
        Assert.Equal(60.0, outcome.GeneratorDowntimeSeconds);
        Assert.Equal(0.50, outcome.GeneratorDerateFraction);
        Assert.Equal(30.0, outcome.GeneratorDerateSeconds);
        Assert.Equal(0.0, outcome.RadiationZoneSeconds);         // E3 无辐射区
        Assert.Equal(0.0, outcome.RadiationEnemySlowFraction);
        Assert.Equal(0.0, outcome.WreckRecoveryCostFraction);
        Assert.Equal(1.00, outcome.LoadRatioAtTrigger, 9);
    }

    [Fact] // legal —— E4 后果列：半径 6/建筑 80% maxHP/污染 60 s/敌 −25% 速/残骸 30%
    public void Meltdown_outcome_carries_SP4_consequence_numbers()
    {
        var engine = new EnergyRiskEngine(Sp4RiskTables.Full());

        // 手算：瞬时 150% 起倒于 tick 1，45 s 倒计于 tick 1351 熔毁
        EnergyRiskOutcome meltdown = Assert.Single(
            Run(engine, 1, 1351, 1.50),
            o => o.Kind == EnergyRiskEventKind.Meltdown);

        Assert.Equal(6.0, meltdown.RadiusCells);
        Assert.Equal(0.80, meltdown.BuildingDamageMaxHpFraction); // 手算：200 HP 建筑受 160 点
        Assert.Equal(0.0, meltdown.EnemyDamage);                  // SP-4 L147 E4 行无对敌伤害列
        Assert.Equal(60.0, meltdown.RadiationZoneSeconds);        // 地面污染 60 s
        Assert.Equal(0.25, meltdown.RadiationEnemySlowFraction);  // 过境敌 −25% 速度
        Assert.Equal(0.30, meltdown.WreckRecoveryCostFraction);   // 残骸修复口径=原成本×30%
        Assert.Equal(0.0, meltdown.GeneratorDowntimeSeconds);     // E4 无停机（直接损毁）
        Assert.Equal(0.0, meltdown.GeneratorDerateFraction);
        Assert.Equal(1.50, meltdown.LoadRatioAtTrigger, 9);
    }

    [Fact] // legal —— E4 留接口证明：Meltdown=null 的表只跑 E3 通道
    public void Null_meltdown_row_disables_the_E4_channel()
    {
        var engine = new EnergyRiskEngine(Sp4RiskTables.OverheatOnly());

        // 150% 满载 3000 tick：若 E4 在场早已熔毁（1350 tick）；仅 E3 在 900/…
        // 周期爆炸（自毁复位后窗口重计）
        List<EnergyRiskOutcome> events = Run(engine, 1, 3000, 1.50);
        Assert.All(events, o => Assert.Equal(EnergyRiskEventKind.OverheatExplosion, o.Kind));
        Assert.Equal(0, engine.MeltdownFireCount);
        Assert.Equal(long.MaxValue, engine.MeltdownSustainTicks); // 无门=永不武装
    }

    // ---- 确定性 ---------------------------------------------------------------

    [Fact] // legal —— 同输入序列 ⇒ 同触发序列+同状态哈希；异输入即时分叉
    public void Same_input_sequence_yields_identical_events_and_hash()
    {
        // 负载序列（确定性算术序列，非随机）：0.9/1.2/1.5/0.6/1.0 循环 ×800 tick
        double[] pattern = { 0.9, 1.2, 1.5, 0.6, 1.0 };
        EnergyRiskEngine RunFresh()
        {
            var engine = new EnergyRiskEngine(Sp4RiskTables.Full());
            var hashes = new List<long>();
            for (long tick = 1; tick <= 4000; tick++)
            {
                double load = pattern[(tick - 1) % pattern.Length];
                _ = engine.OnSettle(tick, 1, load, coolantSupplied: tick % 700 != 0, Culprit);
                hashes.Add(engine.ComputeStateHash());
            }

            return engine;
        }

        EnergyRiskEngine first = RunFresh();
        EnergyRiskEngine second = RunFresh();
        Assert.Equal(first.ComputeStateHash(), second.ComputeStateHash());
        Assert.Equal(first.OverheatFireCount, second.OverheatFireCount);
        Assert.Equal(first.MeltdownFireCount, second.MeltdownFireCount);

        // 分叉证明：同序到 tick 2000 后一路 1.50，触发序列改变、哈希远离
        var divergent = new EnergyRiskEngine(Sp4RiskTables.Full());
        long hashAtDiverge = 0;
        for (long tick = 1; tick <= 4000; tick++)
        {
            double load = tick <= 2000 ? pattern[(tick - 1) % pattern.Length] : 1.50;
            _ = divergent.OnSettle(tick, 1, load, coolantSupplied: tick % 700 != 0, Culprit);
            if (tick == 2000)
            {
                hashAtDiverge = divergent.ComputeStateHash();
            }
        }

        Assert.NotEqual(first.ComputeStateHash(), divergent.ComputeStateHash());
        Assert.NotEqual(0L, hashAtDiverge);
    }

    // ---- 非法输入（确实拒绝） --------------------------------------------------

    [Theory] // illegal —— E3 行非法值逐项被拒
    [InlineData(0.0, 30.0, 100.0, 2.0, 0.10, 1.0, 5.0, 4.0, 0.50, 300.0, 60.0, 0.50, 30.0)] // 阈值 0
    [InlineData(1.0, 0.0, 100.0, 2.0, 0.10, 1.0, 5.0, 4.0, 0.50, 300.0, 60.0, 0.50, 30.0)]   // 窗口 0
    [InlineData(1.0, 30.0, 0.0, 2.0, 0.10, 1.0, 5.0, 4.0, 0.50, 300.0, 60.0, 0.50, 30.0)]    // 热量槽 0
    [InlineData(1.0, 30.0, 100.0, 2.0, 0.0, 1.0, 5.0, 4.0, 0.50, 300.0, 60.0, 0.50, 30.0)]   // 步长 0
    [InlineData(1.0, 30.0, 100.0, 2.0, 0.10, 1.0, 5.0, 0.0, 0.50, 300.0, 60.0, 0.50, 30.0)]  // 半径 0
    [InlineData(1.0, 30.0, 100.0, 2.0, 0.10, 1.0, 5.0, 4.0, 1.50, 300.0, 60.0, 0.50, 30.0)]  // 建筑伤 150%
    [InlineData(1.0, 30.0, 100.0, 2.0, 0.10, 1.0, 5.0, 4.0, 0.50, -300.0, 60.0, 0.50, 30.0)] // 敌伤负
    [InlineData(1.0, 30.0, 100.0, 2.0, 0.10, 1.0, 5.0, 4.0, 0.50, 300.0, 0.0, 0.50, 30.0)]   // 停机 0
    [InlineData(1.0, 30.0, 100.0, 2.0, 0.10, 1.0, 5.0, 4.0, 0.50, 300.0, 60.0, 1.50, 30.0)]  // 降载 150%
    [InlineData(1.0, 30.0, 100.0, 2.0, 0.10, 1.0, 5.0, 4.0, 0.50, 300.0, 60.0, 0.50, 0.0)]   // 降载窗 0
    public void Overheat_row_rejects_illegal_values(
        double threshold, double sustained, double capacity, double baseHeat, double step,
        double stepRate, double cooldown, double radius, double buildingFraction,
        double enemyDamage, double downtime, double derateFraction, double derateSeconds)
    {
        var bad = new OverheatTuning(
            threshold, sustained, capacity, baseHeat, step, stepRate, cooldown,
            radius, buildingFraction, enemyDamage, downtime, derateFraction, derateSeconds);

        Assert.Throws<ArgumentException>(() => new EnergyRiskEngine(new EnergyRiskTuning(bad, Meltdown: null)));
    }

    [Theory] // illegal —— E4 行非法值逐项被拒
    [InlineData(0.0, 20.0, 1.50, 45.0, 0.30, 6.0, 0.80, 60.0, 0.25, 0.30)]  // 持续门 0
    [InlineData(1.2, 0.0, 1.50, 45.0, 0.30, 6.0, 0.80, 60.0, 0.25, 0.30)]   // 持续窗 0
    [InlineData(1.2, 20.0, 1.10, 45.0, 0.30, 6.0, 0.80, 60.0, 0.25, 0.30)]  // 瞬时门 < 持续门（死数据）
    [InlineData(1.2, 20.0, 1.50, 0.0, 0.30, 6.0, 0.80, 60.0, 0.25, 0.30)]   // 倒计时 0
    [InlineData(1.2, 20.0, 1.50, 45.0, 1.30, 6.0, 0.80, 60.0, 0.25, 0.30)]  // 中断削减 130%
    [InlineData(1.2, 20.0, 1.50, 45.0, 0.30, 0.0, 0.80, 60.0, 0.25, 0.30)]  // 半径 0
    [InlineData(1.2, 20.0, 1.50, 45.0, 0.30, 6.0, -0.10, 60.0, 0.25, 0.30)] // 建筑伤负
    [InlineData(1.2, 20.0, 1.50, 45.0, 0.30, 6.0, 0.80, -60.0, 0.25, 0.30)] // 污染负
    [InlineData(1.2, 20.0, 1.50, 45.0, 0.30, 6.0, 0.80, 60.0, 1.25, 0.30)]  // 减速 125%
    [InlineData(1.2, 20.0, 1.50, 45.0, 0.30, 6.0, 0.80, 60.0, 0.25, 3.00)]  // 残骸追回 300%
    public void Meltdown_row_rejects_illegal_values(
        double threshold, double sustained, double instant, double countdown, double interrupt,
        double radius, double buildingFraction, double radiationSeconds, double slowFraction,
        double wreckFraction)
    {
        var bad = new MeltdownTuning(
            threshold, sustained, instant, countdown, interrupt, RequireCoolant: true,
            radius, buildingFraction, radiationSeconds, slowFraction, wreckFraction);

        Assert.Throws<ArgumentException>(() => new EnergyRiskEngine(new EnergyRiskTuning(Sp4RiskTables.Overheat, bad)));
    }

    [Theory] // boundary —— 分量 0 与 1 是合法端点（0%=无降载语义；100%=满额语义）
    [InlineData(0.0, 1.0)]
    [InlineData(1.0, 0.0)]
    public void Overheat_row_accepts_fraction_endpoints(double derateFraction, double buildingFraction)
    {
        var row = Sp4RiskTables.Overheat with
        {
            GeneratorDerateFraction = derateFraction,
            BuildingDamageMaxHpFraction = buildingFraction,
        };
        var engine = new EnergyRiskEngine(new EnergyRiskTuning(row, Meltdown: null)); // 不抛即合法
        Assert.NotNull(engine);
    }

    [Fact] // illegal —— OnSettle 非法入参：dt<1 / 负载率非法
    public void OnSettle_rejects_non_positive_dt_and_bad_load_ratio()
    {
        var engine = new EnergyRiskEngine(Sp4RiskTables.Full());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => engine.OnSettle(10, dtTicks: 0, 1.0, true, Culprit));
        Assert.Throws<ArgumentException>(
            () => engine.OnSettle(10, dtTicks: 1, loadRatio: -0.1, true, Culprit));
        Assert.Throws<ArgumentException>(
            () => engine.OnSettle(10, dtTicks: 1, loadRatio: double.NaN, true, Culprit));
    }

    [Fact] // illegal —— 武装却无肇事机（账本不变量：无生产者⇒负载率必为 0）
    public void Armed_state_without_a_culprit_is_a_wiring_bug()
    {
        var engine = new EnergyRiskEngine(Sp4RiskTables.OverheatOnly());

        _ = Run(engine, 1, 1499, 1.00); // 下一 tick 武装
        Assert.Throws<InvalidOperationException>(
            () => engine.OnSettle(1500, dtTicks: 1, 1.00, true, culprit: null));
    }
}
