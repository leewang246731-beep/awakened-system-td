using EraCore.Contracts.Foundation;
using EraCore.Energy;
using EraCore.Legacy;
using EraCore.Tests.Energy;
using EraCore.Tests.World;
using Xunit;
using static EraCore.Tests.Legacy.LegacyFixture;

namespace EraCore.Tests.Legacy;

// [C-20][F-060 附录 F.3 奇观链第五个] 戴森云=E5 奇观（能源 ×2）的建成效果通道
//（T-134，WonderBoard 四奇观链扩展——WonderThroughputBoard（E3 世博会）/
// WonderRadarSharingBoard（E4 互联网）同纪律的第五块板）：板=奇观表×账本建成
// 位×能源账本的唯一交点（三表互不可知）；倍率族=Π(bonus_value)（wonders.json
// _units 行 "multiplier" 口径——×2 云是翻倍不是三倍）；消费通道=
// EnergyLedger.SetOutputMultiplier（T-117 吞吐乘子通道的能源侧镜像）。
public class WonderEnergyOutputBoardTests
{
    private static WonderSpec Row(int era, string kind, double value) => new(
        $"test:wonder-e{era}", era, $"test:tech-e{era}", $"奇观{era}", $"Wonder {era}",
        Array.Empty<string>(), kind, value);

    private static LegacyLedger LedgerWith(params int[] eras)
    {
        var ledger = new LegacyLedger(Archaeology(), Shop());
        foreach (int era in eras)
        {
            ledger.MarkWonderBuilt($"test:wonder-e{era}");
        }

        return ledger;
    }

    [Fact] // legal — 真表行：戴森云=E5/能源 ×2.0/bonus_kind=energy_output
    public void The_real_wonder_table_carries_the_dyson_cloud_row_per_F3()
    {
        // 附录 F.3：戴森云（E5，能源 ×2）——wonders.json 行逐值（T-124 预置、
        // T-134 消费通道落地）。四奇观链扩展=第五块板：奇观板族现有
        // Throughput（E3）/RadarSharing（E4）/EnergyOutput（E5）+金字塔池上限
        // 项（E1，ResourceStock 内建）——六奇观中五块消费面齐（纪元之门=
        // 通关之门，T-135 终章域）。
        WonderSpec dyson = Wonders().Single(w => w.EraIndex == 5);
        Assert.Equal("campaign:wonder-dyson-cloud", dyson.Id);
        Assert.Equal("戴森云", dyson.NameZh);
        Assert.Equal(WonderEnergyOutputBoard.EnergyOutputBonusKind, dyson.BonusKind);
        Assert.Equal(2.0, dyson.BonusValue, 9); // 能源 ×2（multiplier 口径）
        Assert.Equal("campaign:tech-e5-wonder-dyson-cloud", dyson.TechNode);
    }

    [Fact] // legal — 建成前 1.0、建成后恰 2.0（F.3 冻结值；真表行=campaign:wonder-dyson-cloud）
    public void Multiplier_is_one_without_the_cloud_and_exactly_two_with_it()
    {
        var board = new WonderEnergyOutputBoard(Wonders(), LedgerWith());
        Assert.Equal(1.0, board.Multiplier, 9); // 未建：无加成

        var ledger = new LegacyLedger(Archaeology(), Shop());
        ledger.MarkWonderBuilt(Wonders().Single(w => w.EraIndex == 5).Id); // 真表戴森云行
        Assert.Equal(2.0, new WonderEnergyOutputBoard(Wonders(), ledger).Multiplier, 9); // 能源 ×2
    }

    [Fact] // legal — 倍率族：多行 energy_output 奇观乘法叠加（泛化纪律，非新发明叠加语义）
    public void Multiple_energy_rows_multiply_over_built_ones_only()
    {
        var wonders = new[]
        {
            Row(3, WonderEnergyOutputBoard.EnergyOutputBonusKind, 2.0),
            Row(5, WonderEnergyOutputBoard.EnergyOutputBonusKind, 1.5),
            Row(4, "logistics_throughput", 0.25), // 异类行不进本通道
        };

        Assert.Equal(1.0, new WonderEnergyOutputBoard(wonders, LedgerWith()).Multiplier, 9); // 全未建
        Assert.Equal(2.0, new WonderEnergyOutputBoard(wonders, LedgerWith(3)).Multiplier, 9); // 仅 E3 行
        Assert.Equal(3.0, new WonderEnergyOutputBoard(wonders, LedgerWith(3, 5)).Multiplier, 9); // Π(2.0,1.5)
    }

    [Fact] // illegal — 内容形状守卫：非有限/<1 的倍率行在构造即红（F.3 只增益）
    public void Malformed_energy_rows_reject_at_construction()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new WonderEnergyOutputBoard(
                new[] { Row(5, WonderEnergyOutputBoard.EnergyOutputBonusKind, 0.5) },
                LedgerWith()));
        Assert.Throws<InvalidOperationException>(() =>
            new WonderEnergyOutputBoard(
                new[] { Row(5, WonderEnergyOutputBoard.EnergyOutputBonusKind, double.NaN) },
                LedgerWith()));
        Assert.Throws<ArgumentNullException>(() => new WonderEnergyOutputBoard(null!, LedgerWith()));
        Assert.Throws<ArgumentNullException>(() => new WonderEnergyOutputBoard(Wonders(), null!));
    }

    [Fact] // legal — 板→账本接线：戴森云乘子翻倍发电、减半负载率（E5 湮灭对策面）
    public void The_board_multiplier_flows_into_the_ledger_output_and_load_ratio()
    {
        // 消费通道（板=唯一交点）：SetOutputMultiplier(board.Multiplier) 后——
        // ①总发电翻倍（20→40 点/s，双劳工营 10/s 锚）；②同需求下负载率减半
        //（1.2→0.6，离开 SP-4 湮灭 ≥150% 档）——能源 ×2 的对策面在引擎侧真实生效。
        var clock = new EraCore.Tests.Energy.SteppedClock();
        var ledger = new EnergyLedger(clock);
        ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp); // 10 点/s 锚（era1 L1）
        ledger.RegisterProducer(new EnergyNodeId(2), EnergyNodeKind.LaborCamp);
        ledger.RegisterConsumer(new EnergyNodeId(3), 24.0); // 需求 24：负载 24/20=1.2

        ledger.Settle();
        Assert.Equal(20.0, ledger.TotalOutputPerSecond, 6);
        Assert.Equal(1.2, ledger.LoadRatio, 6);

        var dysonLedger = new LegacyLedger(Archaeology(), Shop());
        dysonLedger.MarkWonderBuilt(Wonders().Single(w => w.EraIndex == 5).Id);
        ledger.SetOutputMultiplier(new WonderEnergyOutputBoard(Wonders(), dysonLedger).Multiplier);
        Assert.Equal(2.0, ledger.OutputMultiplier, 9);

        _ = clock.Advance();
        ledger.Settle();
        Assert.Equal(40.0, ledger.TotalOutputPerSecond, 6); // ×2 发电
        Assert.Equal(0.6, ledger.LoadRatio, 6); // 负载率减半（24/40）
    }

    [Fact] // illegal — 账本通道守卫：<1/非有限的乘子拒收（镜像 TransportTier 吞吐通道）
    public void The_ledger_channel_rejects_sub_unit_and_non_finite_multipliers()
    {
        var ledger = new EnergyLedger(new EraCore.Tests.Energy.SteppedClock());
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.SetOutputMultiplier(0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.SetOutputMultiplier(double.PositiveInfinity));
        Assert.Equal(1.0, ledger.OutputMultiplier, 9); // 拒后保持 1.0
    }

    [Fact] // legal — 确定性：乘子=1.0 时通道哈希中性（E1~E4 位全等），≠1 折入可区分
    public void The_multiplier_folds_into_the_state_hash_only_when_engaged()
    {
        long RunTwoSettles(EnergyLedger ledger, SteppedClock clock)
        {
            ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp);
            ledger.Settle();
            _ = clock.Advance();
            ledger.Settle();
            return ledger.ComputeStateHash();
        }

        // 从未触碰通道的账本 vs 显式 SetOutputMultiplier(1.0) 的账本：同结算序
        // 列 → 哈希全等（=1 不折——E1~E4 位全等纪律）
        var plainClock = new SteppedClock();
        long plain = RunTwoSettles(new EnergyLedger(plainClock), plainClock);
        var neutralClock = new SteppedClock();
        var neutral = new EnergyLedger(neutralClock);
        neutral.SetOutputMultiplier(1.0); // 显式置 1（无奇观局）
        Assert.Equal(plain, RunTwoSettles(neutral, neutralClock));

        // 戴森云 ×2 局：同结算序列哈希可区分（≠1 折入）
        var boostedClock = new SteppedClock();
        var boosted = new EnergyLedger(boostedClock);
        boosted.SetOutputMultiplier(2.0);
        Assert.NotEqual(plain, RunTwoSettles(boosted, boostedClock));
    }
}
