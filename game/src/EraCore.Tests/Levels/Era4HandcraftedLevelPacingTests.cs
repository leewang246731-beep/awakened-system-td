using EraCore.Balance;
using Xunit;
using Xunit.Abstractions;
using static EraCore.Tests.Levels.HandcraftedLevelContent;

namespace EraCore.Tests.Levels;

// [C-19][F-055] T-124 E4 时长验收（中位带）：口径=理论节奏估算
//（LevelPacingEstimator：宽限+Σ波节奏，波节奏=min(波间隔,清场+波谷 15s)，
// 清场=命令流塔线 DPS 对波 EHP 的 F.5 两线式估算；upgrade-tower 沿
// towers.json UpgradesTo 数据链换行）——不含真实玩家操作时间（实测口径归
// T-095）。E4 附加口径：估算器不含弹链供给节流（导弹塔满射速 0.3 件/s/
// 激光塔 1.0 件/s vs 单转换器 0.143/0.25 件/s 产能——波谷蓄弹+杂志缓冲为
// E4 设计现实，见 e4.json _duration_calibration；headless 通关实测见
// Era4LevelPlaythroughTests）。全部数字由 content 表+冻结公式推导（CS-4.4）。
public class Era4HandcraftedLevelPacingTests
{
    private readonly ITestOutputHelper _output;

    public Era4HandcraftedLevelPacingTests(ITestOutputHelper output) => _output = output;

    private static HandcraftedLevelSet E4() => HandcraftedLevelData.Load("e4.json");

    private static HandcraftedLevel Level(int slot) =>
        E4().Items.Single(level => level.Slot == slot);

    private static LevelPacingEstimate Estimate(int slot) =>
        LevelPacingEstimator.Estimate(Level(slot), Towers("era4"), Enemies("era4"));

    public static IEnumerable<object[]> Slots() => Enumerable.Range(1, 5).Select(slot => new object[] { slot });

    // ---- 1) 中位带断言（T-124 验收核心） -----------------------------------

    [Fact]
    public void E4_batch_median_duration_falls_inside_the_15_40_minute_band()
    {
        // [F-055] E4 首批 5 关理论节奏中位 ∈[15,40] 分钟（v3.2 §3 手工关时长带；
        // T-124 任务卡验收），且五关全部落带内（比中位更强的批次断言）。
        var estimates = Enumerable.Range(1, 5).Select(Estimate).OrderBy(estimate => estimate.DurationMinutes).ToList();
        Assert.Equal(5, estimates.Count);

        double median = estimates[2].DurationMinutes; // 五关中位=第三序统计量
        Assert.InRange(median, 15.0, 40.0);
        Assert.All(estimates, estimate => Assert.InRange(estimate.DurationMinutes, 15.0, 40.0));

        foreach (int slot in Enumerable.Range(1, 5))
        {
            _output.WriteLine(
                $"L4-{slot} {Level(slot).NameZh}: win={Level(slot).WinWaves} threat={Level(slot).Threat} " +
                $"interval={Estimate(slot).IntervalSeconds:F0}s grace={Estimate(slot).GraceSeconds:F0}s " +
                $"estimate={Estimate(slot).DurationMinutes:F2} min");
        }
    }

    // ---- 2) 每关节奏结构（宽限/间隔/波谷/不触顶） ---------------------------

    [Theory]
    [MemberData(nameof(Slots))]
    public void Wave_rhythms_stay_below_the_interval_and_never_cap(int slot)
    {
        // [F-055] 设计纪律：教学建造曲线跟得上波曲线——每波 清场+波谷 ≤ 波间隔
        //（F.5 第 30 波验算同族不等式），无一波触顶（触顶=防线溢出的哨兵）。
        HandcraftedLevel level = Level(slot);
        LevelPacingEstimate estimate = Estimate(slot);

        Assert.Equal(level.WinWaves, estimate.Waves.Count); // 每波一行（1..胜波）
        Assert.Equal(Campaign.WinWaves(level.Threat), estimate.WinWaves);
        Assert.Equal(Campaign.WinWaves(level.Threat), level.WinWaves); // §7 双面
        Assert.Equal(
            Campaign.WaveIntervalSeconds(level.Threat) * level.FirstWaveGraceMultiplier,
            estimate.GraceSeconds,
            6);

        foreach (WaveRhythm rhythm in estimate.Waves)
        {
            Assert.InRange(rhythm.RhythmSeconds, level.Waves.ValleySeconds, estimate.IntervalSeconds + 1e-9);
            Assert.False(rhythm.IntervalCapped,
                $"L4-{slot}: 第 {rhythm.Wave} 波触顶（清场 {rhythm.ClearSeconds:F1}s+波谷 > 波间隔 {estimate.IntervalSeconds:F0}s）");
        }

        // 时长恒等式：宽限 + Σ 波节奏
        Assert.Equal(estimate.DurationSeconds, estimate.GraceSeconds + estimate.Waves.Sum(wave => wave.RhythmSeconds), 6);
        Assert.Equal(estimate.DurationSeconds / 60.0, estimate.DurationMinutes, 9);

        // 终局关（slot 5）：第 19 波=Boss 行（常规/精英计数为 0，Boss EHP 独线）
        if (slot == 5)
        {
            WaveRhythm boss = estimate.Waves.Single(wave => wave.IsBossWave);
            Assert.Equal(19, boss.Wave);
            Assert.Equal(0, boss.RegularCount);
            Assert.Equal(0, boss.EliteCount);
        }
    }

    // ---- 3) 数量行=冻结公式复算（防估算器私改波形状） ----------------------

    [Fact]
    public void Elite_and_regular_counts_come_from_the_frozen_formulas()
    {
        // [F-055] 数量行=冻结公式复算（WaveFormula.Count/Elite.Count ×威胁乘子），
        // 抽 L4-3（威胁 0.9、全五线+精英线最长的一关）逐波比对。
        HandcraftedLevel level = Level(3);
        LevelPacingEstimate estimate = LevelPacingEstimator.Estimate(level, Towers("era4"), Enemies("era4"));
        ThreatMultipliers multipliers = Threat.Multipliers(level.Threat);

        foreach (WaveRhythm rhythm in estimate.Waves)
        {
            Assert.Equal(
                WaveFormula.Count(rhythm.Wave, level.Waves.BaseCount, level.Waves.BeginWave,
                    level.Waves.Spacing, level.Waves.Scaling, multipliers.Count),
                rhythm.RegularCount);
            Assert.Equal(Elite.Count(rhythm.Wave), rhythm.EliteCount);
        }

        Assert.Contains(estimate.Waves, wave => wave.EliteCount > 0); // 攻城机甲线确已入场
    }

    // ---- 4) E4 敌特性适配的节奏面（重甲/护盾对塔线的选择压力） --------------

    [Fact]
    public void Armor_lines_drive_the_tower_mix_and_the_upgrade_debuts_late()
    {
        // [F-055][F-032] E4 敌特性适配：甲 18 防爆装甲兵精英线（L4-2）与甲 20
        // 攻城机甲精英线（L4-3）的关必须有晚于首波的导弹增线（160 单发对甲
        // 18/20 保 87.5~88.75% 有效=验证带）；激光塔（T2 激光 96 单发穿甲=
        // 盾对策位）只经 upgrade-tower 在 e4-final w14 入场（升级沿
        // towers.json UpgradesTo 数据链——e2 升级先例；不得直建 tier 2 行）。
        foreach (int slot in new[] { 2, 3 })
        {
            HandcraftedLevel level = Level(slot);
            Assert.Contains(level.CommandScript, step => step.Verb == "build-tower"
                && step.Wave > 1
                && Towers("era4")[step.Tower!].Id == "era4:tower-missile");
        }

        Assert.Equal("era4:enemy-riot-armor", Level(2).Waves.EliteEnemyId); // 甲 18+盾 480 精英线
        Assert.Equal("era4:enemy-siege-mech", Level(3).Waves.EliteEnemyId); // 甲 20 攻城位
        var enemies = Enemies("era4");
        Assert.Equal(18, enemies["era4:enemy-riot-armor"].Armor);
        Assert.Equal(20, enemies["era4:enemy-siege-mech"].Armor);

        // 激光塔=升级步专属（全 e4 文件无 build-tower era4:tower-laser 直建行）
        Assert.All(E4().Items, level => Assert.DoesNotContain(level.CommandScript,
            step => step.Verb == "build-tower" && step.Tower == "era4:tower-laser"));
        HandcraftedLevel final = Level(5);
        ScriptStep upgrade = Assert.Single(final.CommandScript, step => step.Verb == "upgrade-tower");
        Assert.Equal(14, upgrade.Wave); // Boss 波（19）前 5 波窗=电容蓄弹位
        Assert.Equal(2, upgrade.ToTier);

        // 五关估算两两可比（时长表非退化：min≠max——波表/塔线差异真实生效）
        var durations = Enumerable.Range(1, 5).Select(slot => Estimate(slot).DurationMinutes).ToList();
        Assert.True(durations.Max() - durations.Min() > 0.25, "五关时长表不得退化为一列常数");
    }
}
