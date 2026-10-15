using EraCore.Energy;
using Xunit;

namespace EraCore.Tests.Energy;

// [C-08][F-010] EnergyLedger × EnergyRiskEngine 集成（T-113）：负载率口径
// （需求÷有效发电）、E3 爆炸后的停机/降载窗口手算、E4 熔毁注销肇事机、
// 冷却液通道、哈希确定性。SP-4 表值经 Sp4RiskTables 夹具注入（era3.json
// 数据侧归 T-112，本文件不读内容）。
public class EnergyRiskLedgerIntegrationTests
{
    private static EnergyNodeId ProducerId(long v) => new(v);
    private static EnergyNodeId ConsumerId(long v) => new(10_000 + v);

    private static (EnergyLedger Ledger, SteppedClock Clock) LedgerWith(EnergyRiskTuning tuning)
    {
        var clock = new SteppedClock();
        return (new EnergyLedger(clock, risk: new EnergyRiskEngine(tuning)), clock);
    }

    /// <summary>逐 tick 结算 1..ticks，返回各检查点 tick 的（发电/满足度/负载率）快照。</summary>
    private static (Dictionary<long, double> Output, Dictionary<long, double> Satisfaction, Dictionary<long, double> Load) SettleCapturing(
        EnergyLedger ledger, SteppedClock clock, long ticks, long[] checkpoints)
    {
        var outputs = new Dictionary<long, double>();
        var satisfaction = new Dictionary<long, double>();
        var varload = new Dictionary<long, double>();
        for (long tick = 1; tick <= ticks; tick++)
        {
            clock.Advance();
            ledger.Settle();
            if (Array.IndexOf(checkpoints, tick) >= 0)
            {
                outputs[tick] = ledger.TotalOutputPerSecond;
                satisfaction[tick] = ledger.Satisfaction;
                varload[tick] = ledger.LoadRatio;
            }
        }

        return (outputs, satisfaction, varload);
    }

    [Fact] // legal —— 负载率=需求÷有效发电（SP-4 L142），1 前后分别断言
    public void LoadRatio_is_demand_over_effective_output()
    {
        var (ledger, clock) = LedgerWith(Sp4RiskTables.OverheatOnly());
        ledger.RegisterProducer(ProducerId(1), EnergyNodeKind.LaborCamp); // 10 点/s
        ledger.RegisterConsumer(ConsumerId(1), 8.0);

        clock.Advance();
        ledger.Settle();
        Assert.Equal(0.8, ledger.LoadRatio, 9); // 8/10 手算
        Assert.Equal(1.0, ledger.Satisfaction, 9);

        ledger.RegisterConsumer(ConsumerId(2), 2.0); // 需求恰 10 → 满载
        clock.Advance();
        ledger.Settle();
        Assert.Equal(1.0, ledger.LoadRatio, 9);

        ledger.RegisterConsumer(ConsumerId(3), 2.0); // 需求 12 → 120%
        clock.Advance();
        ledger.Settle();
        Assert.Equal(1.2, ledger.LoadRatio, 9);
        Assert.Equal(10.0 / 12.0, ledger.Satisfaction, 9); // clamp(产出/需求)
    }

    [Fact] // legal —— 无风险引擎（E1/E2 risk_events=false）：通道关闭、行为同前
    public void Ledger_without_risk_engine_has_no_risk_channel()
    {
        var clock = new SteppedClock();
        var ledger = new EnergyLedger(clock);
        ledger.RegisterProducer(ProducerId(1), EnergyNodeKind.LaborCamp);
        ledger.RegisterConsumer(ConsumerId(1), 10.0);

        for (long tick = 1; tick <= 2000; tick++)
        {
            clock.Advance();
            ledger.Settle();
        }

        Assert.Null(ledger.Risk);
        Assert.Empty(ledger.RiskEvents);
        Assert.Equal(1.0, ledger.LoadRatio, 9);
        Assert.Equal(10.0, ledger.TotalOutputPerSecond, 9);
    }

    [Fact] // legal + boundary —— E3 爆炸：恰 1500 tick 触发，停机/降载窗口逐 tick 手算
    public void Overheat_explosion_applies_downtime_then_derate_window()
    {
        var (ledger, clock) = LedgerWith(Sp4RiskTables.OverheatOnly());
        ledger.RegisterProducer(ProducerId(1), EnergyNodeKind.LaborCamp); // 10 点/s
        ledger.RegisterConsumer(ConsumerId(1), 10.0);                     // 满载 100%

        // 手算时间线（30 Hz，首个满载结算=tick 1；SP-4 L146）：
        //   爆炸 tick 1500（热量 2/s × 50 s 满；窗口 30 s 已含）
        //   停机 tick 1501..3300（60 s=1800 tick，肇事机 0 输出、负载率 0）
        //   降载 tick 3301..4200（30 s=900 tick，50% 功率=5 点/s、负载 200%）
        //   二次爆炸 tick 4200（降载期负载 2.0：热量 12/s 先满、窗口 900 再咬合）
        long[] checkpoints = { 1499, 1500, 1501, 3300, 3301, 4200, 4201 };
        var (output, satisfaction, load) = SettleCapturing(ledger, clock, 4201, checkpoints);

        // 1499：热量 1499×2/30=99.93 < 100 —— 未触发
        Assert.Equal(10.0, output[1499], 9);

        // 1500：恰满（浮点累加差 ≤1e-9 容差）→ 触发；起爆结算仍按满发口径
        Assert.Equal(10.0, output[1500], 9);
        Assert.Equal(2, ledger.RiskEvents.Count); // 全程恰两次：1500 与 4200（手算）
        EnergyRiskOutcome outcome = ledger.RiskEvents[0];
        Assert.Equal(1500, outcome.Tick);
        Assert.Equal(EnergyRiskEventKind.OverheatExplosion, outcome.Kind);
        Assert.Equal(ProducerId(1), outcome.Culprit);
        Assert.Equal(1.0, satisfaction[1500], 9); // 起爆结算的满足度仍用满发输出

        // 1501：停机开始（后果自下一结算生效）
        Assert.Equal(0.0, output[1501], 9);
        Assert.Equal(0.0, satisfaction[1501], 9);
        Assert.Equal(0.0, load[1501], 9); // 无有效发电 ⇒ 负载率 0（空网不累积）

        Assert.Equal(0.0, output[3300], 9); // 停机第 1800 tick 仍为 0
        Assert.Equal(5.0, output[3301], 9); // 恰 60 s 后转 50% 功率
        Assert.Equal(0.5, satisfaction[3301], 9);
        Assert.Equal(2.0, load[3301], 9); // 10/5 —— 级联过载（真实后果）

        Assert.Equal(10.0, output[4200], 9);    // 二次爆炸结算：窗口被覆写（FireTick=4200，起爆结算口径=满发，同 1500）
        Assert.Equal(EnergyRiskEventKind.OverheatExplosion, ledger.RiskEvents[1].Kind);
        Assert.Equal(4200, ledger.RiskEvents[1].Tick); // 二次爆炸恰在 4200

        Assert.Equal(0.0, output[4201], 9); // 二次爆炸的停机开始
    }

    [Fact] // legal —— 肇事机=最大产能者（并列取先注册）；E3 后果=降载不注销
    public void Culprit_is_the_largest_producer_ties_by_registration()
    {
        var (ledger, clock) = LedgerWith(Sp4RiskTables.OverheatOnly());
        ledger.RegisterProducer(ProducerId(1), EnergyNodeKind.LaborCamp); // 10
        ledger.RegisterProducer(ProducerId(2), EnergyNodeKind.BeastPen);  // 40 ← 肇事（并列在前）
        ledger.RegisterProducer(ProducerId(3), EnergyNodeKind.BeastPen);  // 40（并列在后）
        ledger.RegisterConsumer(ConsumerId(1), 90.0);                     // 90/90 = 满载

        for (long tick = 1; tick <= 1500; tick++)
        {
            clock.Advance();
            ledger.Settle();
        }

        EnergyRiskOutcome outcome = Assert.Single(ledger.RiskEvents);
        Assert.Equal(1500, outcome.Tick);
        Assert.Equal(ProducerId(2), outcome.Culprit);
        Assert.Equal(3, ledger.ProducerCount); // E3 后果=停机/降载，节点仍在册
        Assert.Equal(90.0, ledger.TotalOutputPerSecond, 9); // 起爆结算仍满发

        clock.Advance();
        ledger.Settle();
        // 停机首 tick：10 + 0（肇事机）+ 40 = 50；负载率 90/50=1.8 级联
        Assert.Equal(50.0, ledger.TotalOutputPerSecond, 9);
        Assert.Equal(1.8, ledger.LoadRatio, 9);
    }

    [Fact] // legal + boundary —— E4 熔毁：600 tick 起倒、1950 tick 损毁注销（手算）
    public void Meltdown_permanently_unregisters_the_culprit()
    {
        // 测试隔离（非设计值）：把 E3 窗口推远，单发电机场景下先爆的 E3 停机
        // 会把负载率打回 0、构成熔毁通道的「卸载 ≥30%」中断——混合双通道的
        // 交互属 era4 内容任务（T-123）的场景域，本测试锚定 E4 通道自身表值。
        var (ledger, clock) = LedgerWith(new EnergyRiskTuning(
            Sp4RiskTables.Overheat with { OverloadSustainedSeconds = 100_000.0 },
            Sp4RiskTables.Meltdown));
        ledger.RegisterProducer(ProducerId(1), EnergyNodeKind.LaborCamp); // 10
        ledger.RegisterConsumer(ConsumerId(1), 12.0);                     // 120% 持续门

        // 手算：≥120% 连续 20 s → tick 600 起倒（剩 1350）；45 s 倒计 →
        // tick 1950 熔毁（递减 601..1950 恰 1350 tick）。
        for (long tick = 1; tick <= 1949; tick++)
        {
            clock.Advance();
            ledger.Settle();
        }

        Assert.Empty(ledger.RiskEvents);
        Assert.Equal(1.2, ledger.LoadRatio, 9);
        Assert.Equal(10.0 / 12.0, ledger.Satisfaction, 9);

        clock.Advance();
        ledger.Settle(); // tick 1950

        EnergyRiskOutcome meltdown = Assert.Single(ledger.RiskEvents);
        Assert.Equal(EnergyRiskEventKind.Meltdown, meltdown.Kind);
        Assert.Equal(1950, meltdown.Tick);
        Assert.Equal(ProducerId(1), meltdown.Culprit);
        Assert.Equal(0.30, meltdown.WreckRecoveryCostFraction); // 残骸修复口径（SP-4 L147）

        // 永久损毁：节点注销、输出归零、负载率 0（空网不再累积）
        Assert.Equal(0, ledger.ProducerCount);
        Assert.Equal(0.0, ledger.TotalOutputPerSecond, 9);

        for (long tick = 1951; tick <= 7000; tick++)
        {
            clock.Advance();
            ledger.Settle();
        }

        Assert.Single(ledger.RiskEvents); // 此后无新事件（仍只有 1950 那一次熔毁）
    }

    [Fact] // legal —— 冷却液通道经账本：断冷起倒、恢复中断（任务卡断冷→熔毁+对策）
    public void Coolant_channel_flows_through_the_ledger()
    {
        var (ledger, clock) = LedgerWith(Sp4RiskTables.Full());
        ledger.RegisterProducer(ProducerId(1), EnergyNodeKind.LaborCamp); // 10
        ledger.RegisterConsumer(ConsumerId(1), 5.0);                      // 50% 低载

        for (long tick = 1; tick <= 100; tick++)
        {
            clock.Advance();
            ledger.Settle();
        }

        Assert.Empty(ledger.RiskEvents);

        ledger.SetCoolantSupplied(false); // 断冷 → 起倒（剩 1350）
        for (long tick = 101; tick <= 200; tick++)
        {
            clock.Advance();
            ledger.Settle();
        }

        Assert.Empty(ledger.RiskEvents); // 倒计时中（100 tick < 1350）

        ledger.SetCoolantSupplied(true); // 恢复冷却 → 中断
        for (long tick = 201; tick <= 6000; tick++)
        {
            clock.Advance();
            ledger.Settle();
        }

        Assert.Empty(ledger.RiskEvents); // 对策：供冷+低载 ⇒ 永不熔毁
        Assert.Equal(1, ledger.ProducerCount);
    }

    [Fact] // legal —— 确定性：同操作同哈希；冷却时机不同则恰在分叉点离散
    public void Ledger_hash_is_deterministic_and_diverges_on_different_coolant_timing()
    {
        (EnergyLedger Ledger, SteppedClock Clock) RunCoolantScenario(bool earlyRestore)
        {
            var (ledger, clock) = LedgerWith(Sp4RiskTables.Full());
            ledger.RegisterProducer(ProducerId(1), EnergyNodeKind.LaborCamp);
            ledger.RegisterConsumer(ConsumerId(1), 5.0);
            for (long tick = 1; tick <= 3000; tick++)
            {
                clock.Advance();
                ledger.Settle();
                if (tick == 100)
                {
                    ledger.SetCoolantSupplied(false); // 断冷起倒（tick 100）
                }

                if (tick == (earlyRestore ? 200 : 2500))
                {
                    ledger.SetCoolantSupplied(true); // 恢复时机分叉
                }
            }

            return (ledger, clock);
        }

        // 手算：断冷在 tick 100 结算后才置位 → 首个读到 false 的结算是 101 →
        // 起倒 101、递减 102..1451，晚恢复档 tick 1451 熔毁（2500 恢复来不及）；
        // 早恢复档无事件。
        (EnergyLedger a, _) = RunCoolantScenario(earlyRestore: true);
        (EnergyLedger b, _) = RunCoolantScenario(earlyRestore: true);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash()); // 同输入同哈希
        Assert.Empty(a.RiskEvents);

        (EnergyLedger c, _) = RunCoolantScenario(earlyRestore: false);
        Assert.NotEqual(a.ComputeStateHash(), c.ComputeStateHash());
        Assert.Contains(c.RiskEvents, o => o.Kind == EnergyRiskEventKind.Meltdown && o.Tick == 1451);
    }
}
