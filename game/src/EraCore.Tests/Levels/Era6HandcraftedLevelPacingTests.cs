using EraCore.Balance;
using Xunit;
using Xunit.Abstractions;
using static EraCore.Tests.Levels.HandcraftedLevelContent;

namespace EraCore.Tests.Levels;

// [C-19][F-055] T-135 终章时长验收（中位带）：口径=理论节奏估算
//（LevelPacingEstimator：宽限+Σ波节奏，波节奏=min(波间隔,清场+波谷 15s)，
// 清场=命令流塔线 DPS 对波 EHP 的 F.5 两线式估算）——不含真实玩家操作时间
//（实测口径归 T-095）。终章附加口径：估算器不含弹链供给节流（奇点炮满射速
// 0.6 件/s/双炮 1.2 件/s vs 单奇点弹转换器 0.143 件/s 产能——波谷蓄弹+
// 杂志缓冲为终章设计现实，见 e6.json _duration_calibration；headless 通关
// 实测见 Era6LevelPlaythroughTests）；终章无线内升级步（两塔 max_tier 1
// ——估算器 upgrade 链面退化为纯建造线）。全部数字由 content 表+冻结公式
// 推导（CS-4.4）。
public class Era6HandcraftedLevelPacingTests
{
    private readonly ITestOutputHelper _output;

    public Era6HandcraftedLevelPacingTests(ITestOutputHelper output) => _output = output;

    private static HandcraftedLevelSet E6() => HandcraftedLevelData.Load("e6.json");

    private static HandcraftedLevel Level(int slot) =>
        E6().Items.Single(level => level.Slot == slot);

    private static LevelPacingEstimate Estimate(int slot) =>
        LevelPacingEstimator.Estimate(Level(slot), Towers("era6"), Enemies("era6"));

    public static IEnumerable<object[]> Slots() => Enumerable.Range(1, 5).Select(slot => new object[] { slot });

    // ---- 1) 中位带断言（T-135 验收核心） -----------------------------------

    [Fact]
    public void E6_batch_median_duration_falls_inside_the_15_40_minute_band()
    {
        // [F-055] 终章首批 5 关理论节奏中位 ∈[15,40] 分钟（v3.2 §3 手工关时长带；
        // T-135 任务卡验收同构 e4/e5），且五关全部落带内（比中位更强的批次断言）。
        var estimates = Enumerable.Range(1, 5).Select(Estimate).OrderBy(estimate => estimate.DurationMinutes).ToList();
        Assert.Equal(5, estimates.Count);

        foreach (int slot in Enumerable.Range(1, 5)) // 表先行输出（断言红时仍可读数——诚实披露面）
        {
            _output.WriteLine(
                $"L6-{slot} {Level(slot).NameZh}: win={Level(slot).WinWaves} threat={Level(slot).Threat} " +
                $"interval={Estimate(slot).IntervalSeconds:F0}s grace={Estimate(slot).GraceSeconds:F0}s " +
                $"estimate={Estimate(slot).DurationMinutes:F2} min");
        }

        double median = estimates[2].DurationMinutes; // 五关中位=第三序统计量
        Assert.InRange(median, 15.0, 40.0);
        Assert.All(estimates, estimate => Assert.InRange(estimate.DurationMinutes, 15.0, 40.0));
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
                $"L6-{slot}: 第 {rhythm.Wave} 波触顶（清场 {rhythm.ClearSeconds:F1}s+波谷 > 波间隔 {estimate.IntervalSeconds:F0}s）");
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
        // 抽 L6-3（威胁 0.9、三线+精英线最长的一关）逐波比对。
        HandcraftedLevel level = Level(3);
        LevelPacingEstimate estimate = LevelPacingEstimator.Estimate(level, Towers("era6"), Enemies("era6"));
        ThreatMultipliers multipliers = Threat.Multipliers(level.Threat);

        foreach (WaveRhythm rhythm in estimate.Waves)
        {
            Assert.Equal(
                WaveFormula.Count(rhythm.Wave, level.Waves.BaseCount, level.Waves.BeginWave,
                    level.Waves.Spacing, level.Waves.Scaling, multipliers.Count),
                rhythm.RegularCount);
            Assert.Equal(Elite.Count(rhythm.Wave), rhythm.EliteCount);
        }

        Assert.Contains(estimate.Waves, wave => wave.EliteCount > 0); // 圣盾机甲线确已入场
    }

    // ---- 4) 终章敌特性适配的节奏面（重甲/护盾对塔线的选择压力） --------------

    [Fact]
    public void Armor_lines_drive_the_second_cannon_and_no_upgrade_step_exists()
    {
        // [F-055][F-032] 终章敌特性适配：甲 28 圣盾机甲精英线（L6-2/L6-3）与
        // 甲 32 攻城巨像重甲线（L6-4）的关必须有晚于首波的奇点炮增线（640 单发
        // 对甲 28/32 保 95.6~95% 有效=验证带；L6-3=单炮+时空塔混编线——增线位
        // 由时空塔减速增益补，e6-03 注）；终章无线内升级步（两塔 max_tier 1
        // ——全 e6 文件无 upgrade-tower 行，E5 力场塔『控制场设施不升级』的
        // 整表形态）；时空塔（减速对策位）只在 L6-3 首发。
        foreach (int slot in new[] { 2, 4 })
        {
            HandcraftedLevel level = Level(slot);
            Assert.Contains(level.CommandScript, step => step.Verb == "build-tower"
                && step.Wave > 1
                && Towers("era6")[step.Tower!].Id == "era6:tower-singularity");
        }

        Assert.Equal("era6:enemy-aegis-paladin", Level(2).Waves.EliteEnemyId); // 甲 28+盾 1920 精英线
        Assert.Equal("era6:enemy-aegis-paladin", Level(3).Waves.EliteEnemyId);
        Assert.Equal("era6:enemy-choir-core", Level(4).Waves.EliteEnemyId);   // 甲 26 光环位
        var enemies = Enemies("era6");
        Assert.Equal(28, enemies["era6:enemy-aegis-paladin"].Armor);
        Assert.Equal(32, enemies["era6:enemy-siege-colossus"].Armor);

        // 终章零升级步（max_tier 1 整表形态的命令流面）
        Assert.All(E6().Items, level => Assert.DoesNotContain(level.CommandScript,
            step => step.Verb == "upgrade-tower"));

        // 五关估算两两可比（时长表非退化：min≠max——波表/塔线差异真实生效）
        var durations = Enumerable.Range(1, 5).Select(slot => Estimate(slot).DurationMinutes).ToList();
        Assert.True(durations.Max() - durations.Min() > 0.25, "五关时长表不得退化为一列常数");
    }
}
