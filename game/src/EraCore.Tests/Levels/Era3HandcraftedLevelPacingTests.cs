using EraCore.Balance;
using Xunit;
using Xunit.Abstractions;
using static EraCore.Tests.Levels.HandcraftedLevelContent;

namespace EraCore.Tests.Levels;

// [C-19][F-055] T-116 E3 时长验收（中位带）：口径=理论节奏估算
//（LevelPacingEstimator：宽限+Σ波节奏，波节奏=min(波间隔,清场+波谷 15s)，
// 清场=命令流塔线 DPS 对波 EHP 的 F.5 两线式估算）——不含真实玩家操作时间
//（实测口径归 T-095）。E3 附加口径：估算器不含弹链供给节流（加特林 15 发/s
// 满射速=4.5×单转换器产能——供弹为 E3 设计现实，见 e3.json
// _duration_calibration；headless 通关实测见 Era3LevelPlaythroughTests）。
// 全部数字由 content 表+冻结公式推导（CS-4.4）。
public class Era3HandcraftedLevelPacingTests
{
    private readonly ITestOutputHelper _output;

    public Era3HandcraftedLevelPacingTests(ITestOutputHelper output) => _output = output;

    private static HandcraftedLevelSet E3() => HandcraftedLevelData.Load("e3.json");

    private static HandcraftedLevel Level(int slot) =>
        E3().Items.Single(level => level.Slot == slot);

    private static LevelPacingEstimate Estimate(int slot) =>
        LevelPacingEstimator.Estimate(Level(slot), Towers("era3"), Enemies("era3"));

    public static IEnumerable<object[]> Slots() => Enumerable.Range(1, 4).Select(slot => new object[] { slot });

    // ---- 1) 中位带断言（T-116 验收核心） -----------------------------------

    [Fact]
    public void E3_batch_median_duration_falls_inside_the_15_40_minute_band()
    {
        // [F-055] E3 首批 4 关理论节奏中位 ∈[15,40] 分钟（v3.2 §3 手工关时长带；
        // T-116 任务卡验收），且四关全部落带内（比中位更强的批次断言）。
        var estimates = Enumerable.Range(1, 4).Select(Estimate).OrderBy(estimate => estimate.DurationMinutes).ToList();
        Assert.Equal(4, estimates.Count);

        double median = (estimates[1].DurationMinutes + estimates[2].DurationMinutes) / 2.0;
        Assert.InRange(median, 15.0, 40.0);
        Assert.All(estimates, estimate => Assert.InRange(estimate.DurationMinutes, 15.0, 40.0));

        foreach (int slot in Enumerable.Range(1, 4))
        {
            _output.WriteLine(
                $"L3-{slot} {Level(slot).NameZh}: win={Level(slot).WinWaves} threat={Level(slot).Threat} " +
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
                $"L3-{slot}: 第 {rhythm.Wave} 波触顶（清场 {rhythm.ClearSeconds:F1}s+波谷 > 波间隔 {estimate.IntervalSeconds:F0}s）");
        }

        // 时长恒等式：宽限 + Σ 波节奏
        Assert.Equal(estimate.DurationSeconds, estimate.GraceSeconds + estimate.Waves.Sum(wave => wave.RhythmSeconds), 6);
        Assert.Equal(estimate.DurationSeconds / 60.0, estimate.DurationMinutes, 9);
    }

    // ---- 3) 数量行=冻结公式复算（防估算器私改波形状） ----------------------

    [Fact]
    public void Elite_and_regular_counts_come_from_the_frozen_formulas()
    {
        // [F-055] 数量行=冻结公式复算（WaveFormula.Count/Elite.Count ×威胁乘子），
        // 抽 L3-3（威胁 0.9、全五线+精英线最长的一关）逐波比对。
        HandcraftedLevel level = Level(3);
        LevelPacingEstimate estimate = LevelPacingEstimator.Estimate(level, Towers("era3"), Enemies("era3"));
        ThreatMultipliers multipliers = Threat.Multipliers(level.Threat);

        foreach (WaveRhythm rhythm in estimate.Waves)
        {
            Assert.Equal(
                WaveFormula.Count(rhythm.Wave, level.Waves.BaseCount, level.Waves.BeginWave,
                    level.Waves.Spacing, level.Waves.Scaling, multipliers.Count),
                rhythm.RegularCount);
            Assert.Equal(Elite.Count(rhythm.Wave), rhythm.EliteCount);
        }

        Assert.Contains(estimate.Waves, wave => wave.EliteCount > 0); // 蒸汽冲车线确已入场
    }

    // ---- 4) E3 敌特性适配的节奏面（护盾/重甲对塔线的选择压力） --------------

    [Fact]
    public void Armor_lines_drive_the_tower_mix_and_shield_level_paces_on_pierce_only()
    {
        // [F-055][F-032] E3 敌特性适配：甲 16 蒸汽冲车精英线（L3-2/L3-3）的关
        // 必须有臼炮（480 单发对甲 16 ≈96.7% 有效=反重甲位）且晚于首波（弹链
        // 供给节奏位）；护盾关 L3-4 全穿刺线（c2）对甲 12 精英——臼炮缺席即
        // c2 的内容面（装载器 Set 测试同锚）。
        var towers = Towers("era3");
        foreach (int slot in new[] { 2, 3 })
        {
            HandcraftedLevel level = Level(slot);
            Assert.Equal("era3:enemy-ram", level.Waves.EliteEnemyId); // 甲 16 精英线
            Assert.Contains(level.CommandScript, step => step.Verb == "build-tower"
                && towers[step.Tower!].Id == "era3:tower-mortar"
                && step.Wave > 1);
        }

        HandcraftedLevel shield = Level(4);
        Assert.DoesNotContain(shield.CommandScript, step => step.Verb == "build-tower"
            && towers[step.Tower!].Class.ToString().Equals("splash", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("era3:enemy-ironclad-guard", shield.Waves.EliteEnemyId);

        // 四关估算两两可比（时长表非退化：min≠max——波表/塔线差异真实生效）
        var durations = Enumerable.Range(1, 4).Select(slot => Estimate(slot).DurationMinutes).ToList();
        Assert.True(durations.Max() - durations.Min() > 0.25, "四关时长表不得退化为一列常数");
    }
}
