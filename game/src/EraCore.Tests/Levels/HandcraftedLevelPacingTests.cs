using EraCore.Balance;
using Xunit;
using static EraCore.Tests.Levels.HandcraftedLevelContent;

namespace EraCore.Tests.Levels;

// [C-19][F-055] T-085 时长验收（中位带）+ LevelPacingEstimator 行为回归：
// 口径=理论节奏（宽限+Σ波节奏，波节奏=min(波间隔,清场+波谷)，清场=命令流塔线
// DPS 对波 EHP 的 F.5 两线式估算）——不含真实玩家操作时间（实测口径归 T-095）。
// 全部数字由 content 表+冻结公式推导（CS-4.4）。
public class HandcraftedLevelPacingTests
{
    private static readonly (string Id, string EraDir)[] Batch =
    {
        ("campaign:level-e1-01", "era1"),
        ("campaign:level-e1-02", "era1"),
        ("campaign:level-e1-03", "era1"),
        ("campaign:level-e1-04", "era1"),
        ("campaign:level-e1-final", "era1"),
        ("campaign:level-e2-01", "era2"),
        ("campaign:level-e2-02", "era2"),
        ("campaign:level-e2-03", "era2"),
        ("campaign:level-e2-final", "era2"),
    };

    private static HandcraftedLevel Level(string id) =>
        HandcraftedLevelData.Load(id.Contains("level-e1-", StringComparison.Ordinal) ? "e1.json" : "e2.json")
            .TryGet(id) ?? throw new InvalidOperationException($"缺关卡行 {id}");

    private static LevelPacingEstimate Estimate(string id)
    {
        HandcraftedLevel level = Level(id);
        return LevelPacingEstimator.Estimate(
            level, Towers(EraDirOf(level)), Enemies(EraDirOf(level)));
    }

    private static string EraDirOf(HandcraftedLevel level) => EraDir(level);

    // ---- 1) 中位带断言（T-085 验收核心） -----------------------------------

    [Fact]
    public void First_batch_median_duration_falls_inside_the_15_40_minute_band()
    {
        // [F-055] 首批 7 关+两终局关理论节奏中位 ∈[15,40] 分钟（v3.2 §3 手工关
        // 时长带；T-085 任务卡验收+跃迁前置补全），且九关全部落带内（比中位
        // 更强的批次断言）。终局关 19 波=首批最长胜波，但其 Boss 波只有 Boss
        // 批次一行清场（估算器 boss 波语义），估算不因胜波变长而失控。
        var estimates = Batch.Select(row => Estimate(row.Id)).OrderBy(e => e.DurationMinutes).ToList();
        Assert.Equal(9, estimates.Count);

        double median = estimates[4].DurationMinutes;
        Assert.InRange(median, 15.0, 40.0);
        Assert.All(estimates, e => Assert.InRange(e.DurationMinutes, 15.0, 40.0));

        // 中位锚值面（理论节奏表，口径见 LevelPacingEstimator 头注）：
        // 排序后 15.42(e2-02) / 16.46 / 16.73 / 16.76(e2-final) / 17.67 /
        // 18.73 / 19.37(e2-03) / 19.84 / 21.72(e1-final) 分钟。
        Assert.InRange(estimates[0].DurationMinutes, 15.0, 16.0);
        Assert.InRange(median, 17.0, 18.5);
        Assert.InRange(estimates[8].DurationMinutes, 21.0, 22.0);
    }

    [Fact]
    public void Every_E1_level_paces_inside_the_15_25_minute_band()
    {
        // [F-055] T-085 任务卡：E1 首批每关 15~25 分钟节奏（胜波按 WinWaves(t)）。
        foreach (string id in Batch.Where(b => b.EraDir == "era1").Select(b => b.Id))
        {
            LevelPacingEstimate estimate = Estimate(id);
            Assert.InRange(estimate.DurationMinutes, 15.0, 25.0);
            Assert.Equal(Campaign.WinWaves(Level(id).Threat), estimate.WinWaves);
        }
    }

    // ---- 2) 每关节奏结构（宽限/间隔/波谷/不触顶） ---------------------------

    public static IEnumerable<object[]> BatchIds() => Batch.Select(b => new object[] { b.Id });

    [Theory]
    [MemberData(nameof(BatchIds))]
    public void Wave_rhythms_stay_below_the_interval_and_never_cap(string id)
    {
        // [F-055] 设计纪律：教学建造曲线跟得上波曲线——每波 清场+波谷 ≤ 波间隔
        //（F.5 第 30 波验算同族不等式），无一波触顶（触顶=防线溢出的哨兵）。
        HandcraftedLevel level = Level(id);
        LevelPacingEstimate estimate = Estimate(id);

        Assert.Equal(level.WinWaves, estimate.Waves.Count); // 每波一行（1..胜波）
        Assert.Equal(Campaign.WinWaves(level.Threat), estimate.WinWaves);
        Assert.Equal(
            Campaign.WaveIntervalSeconds(level.Threat) * level.FirstWaveGraceMultiplier,
            estimate.GraceSeconds,
            6);

        foreach (WaveRhythm rhythm in estimate.Waves)
        {
            Assert.InRange(rhythm.RhythmSeconds, level.Waves.ValleySeconds, estimate.IntervalSeconds + 1e-9);
            Assert.False(rhythm.IntervalCapped, $"{id}: 第 {rhythm.Wave} 波触顶（清场 {rhythm.ClearSeconds:F1}s+波谷 > 波间隔 {estimate.IntervalSeconds:F0}s）");
            Assert.Equal(rhythm.Wave, estimate.Waves[rhythm.Wave - 1].Wave);
        }

        // 时长恒等式：宽限 + Σ 波节奏
        Assert.Equal(estimate.DurationSeconds,
            estimate.GraceSeconds + estimate.Waves.Sum(w => w.RhythmSeconds), 6);
        Assert.Equal(estimate.DurationSeconds / 60.0, estimate.DurationMinutes, 9);
    }

    [Theory]
    [MemberData(nameof(BatchIds))]
    public void C1_timed_challenge_is_achievable_within_80_percent_of_budget(string id)
    {
        // [F-055] 携带 SP-8b C1（限时：≤预算×80%）的关，理论节奏须 ≤ 预算×0.8
        //（目标不是空头支票；threshold=预算乘子 0.8，objectives.json 判定约定）。
        HandcraftedLevel level = Level(id);
        if (!level.Objectives.Challenge.Contains("campaign:obj-c1"))
        {
            Assert.DoesNotContain("campaign:obj-c1", level.Objectives.Challenge); // negative 面锚
            return;
        }

        LevelPacingEstimate estimate = Estimate(id);
        Assert.True(estimate.DurationSeconds <= level.BudgetSeconds * 0.8,
            $"{id}: 理论节奏 {estimate.DurationSeconds:F0}s > C1 阈值 {level.BudgetSeconds * 0.8:F0}s");
    }

    // ---- 3) 估算器边界/非法/确定性（CS-3.1） -------------------------------

    [Theory]
    [InlineData("campaign:level-e1-final")]
    [InlineData("campaign:level-e2-final")]
    public void Boss_wave_line_models_the_boss_batch_alone(string id)
    {
        // [F-055] legal — 终局关 Boss 波（spawner 语义：该波只出 Boss 批次，
        // 常规/精英线压掉）：估算行 IsBossWave=true、常规/精英计数=0、清场=
        // Boss EHP 线（HP×威胁 HP 乘子×Boss 状态 ×1.5）÷ 穿甲后塔线 DPS——
        // 手算复核（E1：2500×1.1667×1.5=4375；E2：4000×1.1667×1.5=7000）。
        HandcraftedLevel level = Level(id);
        LevelPacingEstimate estimate = Estimate(id);

        WaveRhythm bossLine = estimate.Waves.Single(w => w.IsBossWave);
        Assert.Equal(level.Waves.BossWave, bossLine.Wave);
        Assert.Equal(0, bossLine.RegularCount); // 该波常规线不入场
        Assert.Equal(0, bossLine.EliteCount); // 精英线同波压掉（E2 ram 线 w19=0）
        Assert.All(
            estimate.Waves.Where(w => !w.IsBossWave),
            w => Assert.Equal(Elite.Count(w.Wave) > 0 && level.Waves.EliteLineEnabled
                ? Elite.Count(w.Wave)
                : 0, w.EliteCount)); // 非 Boss 波精英线照常（E2 侧 w12 起 2/只）

        var enemies = Enemies(EraDirOf(level));
        double bossHp = enemies[level.Waves.BossEnemyId!].Hp
            * Threat.Multipliers(level.Threat).Hp
            * 1.5; // v3.2 §6 Boss 状态 ×1.5（WaveSpawnConfig 默认值同源）
        var towers = Towers(EraDirOf(level));
        Assert.True(bossLine.ClearSeconds > 0);
        Assert.True(
            bossLine.ClearSeconds <= bossHp / (0.1 * towers.Values.Sum(t => t.Dps)) + 1e-9,
            "Boss 波清场不得超过 10% 保底 DPS 口径的上界（甲>单发时的保守下限）");
    }

    [Fact]
    public void Towerless_script_caps_every_wave_at_the_interval()
    {
        // [F-055] boundary — 无伤害塔的脚本：清场=∞，每波节奏触顶=波间隔，总时长=
        // 宽限+胜波×间隔（估算器上界行为，不是合法关卡形态——首波伤害塔
        // 不变式由 HandcraftedLevelSetTests 把关）。
        HandcraftedLevel level = Level("campaign:level-e1-01") with
        {
            CommandScript = new[]
            {
                new ScriptStep(1, "build-labor-camp", new CellPoint(1, 1), null, null, null, null, null, null, null, null, null, null),
            },
        };
        LevelPacingEstimate estimate = LevelPacingEstimator.Estimate(
            level, Towers("era1"), Enemies("era1"));

        Assert.All(estimate.Waves, w =>
        {
            Assert.True(w.IntervalCapped);
            Assert.Equal(estimate.IntervalSeconds, w.RhythmSeconds, 9);
            Assert.True(double.IsInfinity(w.ClearSeconds));
        });
        Assert.Equal(
            estimate.GraceSeconds + (estimate.WinWaves * estimate.IntervalSeconds),
            estimate.DurationSeconds, 9);
    }

    [Fact]
    public void Upgrade_step_past_the_data_chain_is_rejected()
    {
        // [F-055] illegal — 升级目标档越过 towers.json UpgradesTo 数据链（rockfall 无后继行）。
        HandcraftedLevel level = Level("campaign:level-e1-01") with
        {
            CommandScript = new[]
            {
                new ScriptStep(1, "build-tower", new CellPoint(6, 21), "era1:tower-rockfall", null, null, null, null, null, null, null, null, null),
                new ScriptStep(2, "upgrade-tower", null, null, null, null, null, null, 2, 2, null, null, null),
            },
        };
        Assert.Throws<HandcraftedLevelSchemaException>(() =>
            LevelPacingEstimator.Estimate(level, Towers("era1"), Enemies("era1")));
    }

    [Fact]
    public void Dangling_content_reference_fails_fast()
    {
        // [F-055] illegal — 塔行不在内容表（引用闭合的估算器面：未解析即抛）。
        HandcraftedLevel level = Level("campaign:level-e1-01") with
        {
            CommandScript = new[]
            {
                new ScriptStep(1, "build-tower", new CellPoint(6, 21), "era1:tower-bogus", null, null, null, null, null, null, null, null, null),
            },
        };
        Assert.Throws<KeyNotFoundException>(() =>
            LevelPacingEstimator.Estimate(level, Towers("era1"), Enemies("era1")));
    }

    [Fact]
    public void Estimating_the_same_level_twice_yields_identical_results()
    {
        // [F-055] 确定性②：同关卡双估算逐值相等（WaveRhythm 全标量成员，记录
        // 相等性即逐字段等值；汇总行携带列表故拆标量+逐波比对）。
        foreach (string id in Batch.Select(b => b.Id))
        {
            HandcraftedLevel level = Level(id);
            var towerTable = Towers(EraDirOf(level));
            var enemyTable = Enemies(EraDirOf(level));
            LevelPacingEstimate first = LevelPacingEstimator.Estimate(level, towerTable, enemyTable);
            LevelPacingEstimate second = LevelPacingEstimator.Estimate(level, towerTable, enemyTable);

            Assert.Equal(first.WinWaves, second.WinWaves);
            Assert.Equal(first.IntervalSeconds, second.IntervalSeconds);
            Assert.Equal(first.GraceSeconds, second.GraceSeconds);
            Assert.Equal(first.DurationSeconds, second.DurationSeconds);
            Assert.Equal(first.Waves.Count, second.Waves.Count);
            for (int i = 0; i < first.Waves.Count; i++)
            {
                Assert.Equal(first.Waves[i], second.Waves[i]); // 逐波记录全等
            }
        }
    }

    [Fact]
    public void Elite_and_regular_counts_come_from_the_frozen_formulas()
    {
        // [F-055] 数量行=冻结公式复算（WaveFormula.Count/Elite.Count ×威胁乘子），
        // 防估算器私改波形状。已发行 E2 行不启用精英线（跃迁前置补全批裁定：
        // 攻城锤甲 12 无箭弹链可清），故精英线以 L2-03 合成精英行复算（with 注入
        // ——估算器行为回归口径，非内容行）。
        HandcraftedLevel level = Level("campaign:level-e2-03") with
        {
            Waves = Level("campaign:level-e2-03").Waves with
            {
                EliteLineEnabled = true,
                EliteEnemyId = "era2:enemy-ram",
            },
        };
        LevelPacingEstimate estimate = LevelPacingEstimator.Estimate(
            level, Towers("era2"), Enemies("era2"));
        ThreatMultipliers multipliers = Threat.Multipliers(level.Threat);

        foreach (WaveRhythm rhythm in estimate.Waves)
        {
            Assert.Equal(
                WaveFormula.Count(rhythm.Wave, level.Waves.BaseCount, level.Waves.BeginWave,
                    level.Waves.Spacing, level.Waves.Scaling, multipliers.Count),
                rhythm.RegularCount);
            Assert.Equal(Elite.Count(rhythm.Wave), rhythm.EliteCount);
        }

        Assert.Contains(estimate.Waves, w => w.EliteCount > 0); // 攻城锤线确已入场（合成行）
    }
}
