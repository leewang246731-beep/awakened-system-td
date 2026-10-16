using EraCore.Balance;
using EraCore.Combat;
using EraCore.Units;

namespace EraCore.Tests.Levels;

/// <summary>Per-wave rhythm line of one estimate: counts, clear time and the capped flag.</summary>
public sealed record WaveRhythm(
    int Wave,
    int RegularCount,
    int EliteCount,
    double ClearSeconds,
    double RhythmSeconds,
    bool IntervalCapped,
    bool IsBossWave = false);

/// <summary>
/// Result of one headless pacing estimate (T-085 时长验收): the wave
/// schedule numbers plus the total theoretical duration.
/// </summary>
/// <param name="WinWaves">胜波 (formula value of the level threat).</param>
/// <param name="IntervalSeconds">波间隔 = 120s×间隔乘子 (v3.2 §6 / F.2).</param>
/// <param name="GraceSeconds">首波宽限 = 宽限乘子 × 波间隔（教学 ×3）.</param>
/// <param name="Waves">One rhythm line per wave 1..胜波.</param>
/// <param name="DurationSeconds">宽限 + Σ 波节奏（秒）.</param>
/// <param name="DurationMinutes">DurationSeconds ÷ 60.</param>
public sealed record LevelPacingEstimate(
    int WinWaves,
    double IntervalSeconds,
    double GraceSeconds,
    IReadOnlyList<WaveRhythm> Waves,
    double DurationSeconds,
    double DurationMinutes);

/// <summary>
/// Headless 自动通关节奏估算器 (T-085 验收核心, F-055)：按命令流+波间隔公式
/// 估算每关时长。<b>口径=理论节奏</b>，不是真实玩家操作时间（实测口径归
/// T-095 的 ≥3 人中位）：
/// <list type="number">
/// <item>波节奏 = min(波间隔, 清场时间 + 波谷 15s)——波间隔公式
/// <see cref="Campaign.WaveIntervalSeconds"/>(t)=120s×乘子（F.2 表）是每波节奏的设计上界；</item>
/// <item>清场时间 = 两线式 Σ EHP线÷有效DPS线（F.5 第 30 波验算先例：散兵线+攻城线逐线清场）；</item>
/// <item>EHP线 = 数量×HP×HP 乘子（<see cref="Threat.Multipliers"/>），数量=<see cref="WaveFormula.Count"/>+<see cref="Elite.Count"/>（冻结公式）；</item>
/// <item>有效 DPS = 命令流塔线 DPS（升级沿 towers.json UpgradesTo 数据链换行）×穿甲项 max(d̄−甲,10%×d̄)/d̄（v3.2 §6 伤害式，d̄=DPS 加权平均单发）；</item>
/// <item>命令流建造步骤不另占节奏时间（在波谷与宽限内完成）；英雄/卷轴增益不计入（保守口径）。</item>
/// </list>
/// 全部数值出自 content 表与冻结公式（CS-2.3/CS-4.4），估算器自身不携带设计数值。
/// </summary>
internal static class LevelPacingEstimator
{
    /// <summary>
    /// Boss-state hp multiplier of the v3.2 §6 Boss 状态 (×1.5) — same frozen
    /// source as the <c>WaveSpawnConfig.BossHpMultiplier</c> default the live
    /// spawner applies; restated here because the estimator reads content
    /// tables, not engine configs (T-085 口径，与 BossBattleIntegrationTests
    /// 手算行 2500×1.5 同锚).
    /// </summary>
    private const double BossHpMultiplier = 1.5;

    /// <summary>
    /// Estimates the theoretical rhythm of one handcrafted level. Tower and
    /// enemy specs must resolve every id the level references (callers feed
    /// the era content tables); a dangling id throws
    /// <see cref="KeyNotFoundException"/>. Boss waves (终局关行
    /// boss_wave/boss_enemy_id) follow the spawner's boss-wave semantics:
    /// the wave materializes the boss batch ALONE (regular/elite lines are
    /// suppressed that wave — <see cref="EraCore.Units.WaveSpawner.PlanWave"/>),
    /// so its clear time is the boss EHP line (HP×威胁 HP 乘子×Boss 状态
    /// ×1.5) against the tower line's armor-pierced DPS.
    /// </summary>
    public static LevelPacingEstimate Estimate(
        HandcraftedLevel level,
        IReadOnlyDictionary<string, TowerSpec> towers,
        IReadOnlyDictionary<string, EnemySpec> enemies)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(towers);
        ArgumentNullException.ThrowIfNull(enemies);

        ThreatMultipliers multipliers = Threat.Multipliers(level.Threat);
        double interval = Campaign.WaveIntervalSeconds(level.Threat);
        double grace = interval * level.FirstWaveGraceMultiplier;
        int winWaves = Campaign.WinWaves(level.Threat);
        double valley = level.Waves.ValleySeconds;

        // Tower line over time: entity ordinal → current tower row id
        // (upgrades walk the towers.json UpgradesTo chain — data wiring).
        var towerRows = new Dictionary<long, string>();
        long nextEntity = 2; // warehouse = 1 (Simulation convention)
        var steps = level.CommandScript.OrderBy(step => step.Wave).ToList();
        int cursor = 0;

        var lines = new List<WaveRhythm>();
        double total = 0.0;
        for (int wave = 1; wave <= winWaves; wave++)
        {
            while (cursor < steps.Count && steps[cursor].Wave <= wave)
            {
                ScriptStep step = steps[cursor++];
                switch (step.Verb)
                {
                    case "build-tower":
                        towerRows[nextEntity++] = step.Tower!;
                        break;
                    case "build-labor-camp" or "build-harvester" or "build-converter":
                        nextEntity++;
                        break;
                    case "upgrade-tower":
                        string row = ResolveUpgradeChain(level, towers, towerRows[step.Entity!.Value], step.ToTier!.Value);
                        towerRows[step.Entity!.Value] = row;
                        break;
                }
            }

            double dpsTotal = 0.0;
            double shotWeighted = 0.0;
            foreach (string rowId in towerRows.Values)
            {
                TowerSpec spec = towers[rowId];
                dpsTotal += spec.Dps;
                shotWeighted += spec.Dps * spec.DamagePerShot;
            }

            double meanShot = dpsTotal > 0.0 ? shotWeighted / dpsTotal : 0.0;
            double clear = dpsTotal > 0.0 ? 0.0 : double.PositiveInfinity;

            bool isBossWave = level.Waves.BossEnemyId is not null && wave == level.Waves.BossWave;
            if (isBossWave)
            {
                // Boss 波只出 Boss 批次（spawner 语义）：清场=Boss EHP 线，
                // 常规/精英计数该波为 0（下述两线不再加项）。
                if (dpsTotal > 0.0)
                {
                    EnemySpec boss = enemies[level.Waves.BossEnemyId!];
                    clear += boss.Hp * multipliers.Hp * BossHpMultiplier
                        / EffectiveDps(dpsTotal, meanShot, boss.Armor);
                }
            }

            int regular = WaveFormula.Count(
                wave, level.Waves.BaseCount, level.Waves.BeginWave, level.Waves.Spacing,
                level.Waves.Scaling, multipliers.Count);
            int rosterSize = level.Waves.RegularRoster.Count;
            if (!isBossWave)
            {
                for (int i = 0; i < rosterSize; i++)
                {
                    int count = (regular / rosterSize) + (i < regular % rosterSize ? 1 : 0);
                    if (count == 0 || double.IsInfinity(clear))
                    {
                        continue;
                    }

                    EnemySpec enemy = enemies[level.Waves.RegularRoster[i]];
                    clear += count * enemy.Hp * multipliers.Hp / EffectiveDps(dpsTotal, meanShot, enemy.Armor);
                }
            }

            int elite = level.Waves.EliteLineEnabled ? Elite.Count(wave) : 0;
            if (!isBossWave && elite > 0)
            {
                if (double.IsInfinity(clear))
                {
                    // keep infinity: no tower line at all this wave
                }
                else
                {
                    EnemySpec enemy = enemies[level.Waves.EliteEnemyId!];
                    clear += elite * enemy.Hp * multipliers.Hp / EffectiveDps(dpsTotal, meanShot, enemy.Armor);
                }
            }

            double rhythm = Math.Min(interval, clear + valley);
            total += rhythm;
            lines.Add(new WaveRhythm(
                wave,
                isBossWave ? 0 : regular,
                isBossWave ? 0 : elite,
                clear,
                rhythm,
                clear + valley > interval,
                isBossWave));
        }

        double duration = grace + total;
        return new LevelPacingEstimate(winWaves, interval, grace, lines, duration, duration / 60.0);
    }

    /// <summary>有效 DPS：穿甲项 max(d̄−甲,10%×d̄)/d̄ 作用于整条 DPS 线（v3.2 §6 伤害式）。</summary>
    private static double EffectiveDps(double dpsTotal, double meanShot, int armor) =>
        dpsTotal * Math.Max(meanShot - armor, 0.1 * meanShot) / meanShot;

    private static string ResolveUpgradeChain(
        HandcraftedLevel level,
        IReadOnlyDictionary<string, TowerSpec> towers,
        string fromRow,
        int toTier)
    {
        string row = fromRow;
        for (int i = 1; i < toTier; i++)
        {
            string? next = towers[row].UpgradesTo;
            if (string.IsNullOrEmpty(next))
            {
                throw new HandcraftedLevelSchemaException(
                    $"{level.Id}: 升级链 {fromRow} 短于目标档 {toTier}（towers.json UpgradesTo 断链）");
            }

            row = next;
        }

        return row;
    }
}
