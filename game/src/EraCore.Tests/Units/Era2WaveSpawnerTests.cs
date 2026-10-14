using EraCore.Balance;
using EraCore.Contracts.Foundation;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Units;

// [F-031] [F-035] E2 波刷接入（T-066）：攻城锤精英线全参数化——数量乘子恒 1
// 断言（威胁数量乘子永不进精英线，v3.2 §6）、第 20 波 F.5 锚点 4 锤、
// E2 无波次盾（护盾 E3 起）+个体盾框架位接线。
public class Era2WaveSpawnerTests
{
    private const int CellSize = 32;

    private static WaveSpawnConfig NewConfig(
        double threat = 0.75,
        bool elite = true,
        int? shieldStartWave = null,
        string rosterEnemy = "era2:enemy-skirmisher") =>
        new(
            BaseCount: 10, BeginWave: 5, Spacing: 5, Scaling: 2.0, ThreatLevel: threat,
            RegularRoster: new[] { rosterEnemy },
            EliteLineEnabled: elite,
            EliteEnemyId: elite ? "era2:enemy-ram" : null,
            BossWave: 30, BossEnemyId: "era2:enemy-boss",
            BatchesPerWave: 1, // 单批全量入场：Poll(0) 即足额（批量节奏由 WaveSpawnerTests 覆盖）
            EntryCells: new[] { new CellCoord(6, 0) },
            ShieldStartWave: shieldStartWave);

    private static WaveSpawner NewSpawner(WaveSpawnConfig config) =>
        new(UnitsContent.LoadEra2EnemyCatalog(), config, new SplitMixRng(11), new FixedClock());

    // ---- 精英线：数量乘子恒 1 ----------------------------------------------

    [Theory] // legal — 精英计数=Elite.Count 公式：1+⌊(波−8)/4⌋（v3.2 §6）
    [InlineData(7, 0)]   // ⌊−1/4⌋=−1 → 夹 0（第 8 波首发前为 0）
    [InlineData(8, 1)]   // 首发波恰 1
    [InlineData(9, 1)]
    [InlineData(11, 1)]
    [InlineData(12, 2)]  // 1+⌊4/4⌋=2
    [InlineData(16, 3)]
    [InlineData(20, 4)]  // F.5 第 20 波：4 攻城锤（SP-3c L131）
    [InlineData(28, 6)]
    [InlineData(40, 9)]  // 1+⌊32/4⌋=9
    [InlineData(44, 10)] // 无尽段不封顶（SP-7a）
    public void Elite_line_follows_the_independent_formula(int wave, int expectedElite)
    {
        WavePlan plan = NewSpawner(NewConfig()).PlanWave(wave, startTick: 0);
        Assert.Equal(expectedElite, plan.EliteCount);
        Assert.Equal(Elite.Count(wave), plan.EliteCount);
        Assert.Equal(plan.TotalCount, plan.RegularCount + plan.EliteCount + (plan.HasBoss ? 1 : 0));
    }

    [Theory] // legal — 数量乘子恒 1：威胁抬升常规线、精英线纹丝不动（核心断言）
    [InlineData(0.3)]   // 低威胁：数量乘子 0.5
    [InlineData(0.75)]  // 数量乘子 1.25
    [InlineData(1.2)]   // 高威胁：数量乘子 2.0
    public void Threat_count_multiplier_never_scales_the_elite_line(double threat)
    {
        ThreatMultipliers multipliers = Threat.Multipliers(threat);
        WavePlan plan = NewSpawner(NewConfig(threat)).PlanWave(20, startTick: 0);

        // 常规线确实随威胁伸缩（乘子生效的对照面；单层 floor 与库同式）
        Assert.Equal(WaveFormula.Count(20, 10, 5, 5, 2.0, multipliers.Count), plan.RegularCount);
        Assert.NotEqual(WaveFormula.Count(20, 10, 5, 5, 2.0, 1.0), plan.RegularCount); // 乘子≠1 必须可见伸缩

        // 精英线：任何威胁下恒 4（第 20 波锚点）
        Assert.Equal(4, plan.EliteCount);
    }

    [Fact] // legal — 实刷的精英单位携带攻城锤数据（HP/甲/内容 id/IsElite）
    public void Spawned_elites_carry_the_ram_spec()
    {
        WaveSpawner spawner = NewSpawner(NewConfig());
        spawner.BeginWave(20, startTick: 0);
        IReadOnlyList<Unit> spawned = spawner.Poll(0);

        var elites = spawned.Where(u => u.IsElite).ToList();
        Assert.Equal(4, elites.Count);
        Assert.All(elites, e => Assert.Equal("era2:enemy-ram", e.ContentId));
        Assert.All(elites, e => Assert.Equal(1500, e.MaxHp)); // 威胁 0.75 → HP 乘子 1.0
        Assert.All(elites, e => Assert.Equal(12, e.Armor));
        Assert.All(elites, e => Assert.False(e.IsBoss));
    }

    // ---- E2 无波次盾 + 框架位 --------------------------------------------

    [Fact] // legal — E2 实刷单位全部无盾（ShieldStartWave=null；SP-3b 无个体盾字段）
    public void Era2_spawns_carry_no_shield()
    {
        WaveSpawner spawner = NewSpawner(NewConfig());
        spawner.BeginWave(20, startTick: 0);
        IReadOnlyList<Unit> spawned = spawner.Poll(0);

        Assert.NotEmpty(spawned);
        Assert.All(spawned, u => Assert.Equal(0, u.Shield));
    }

    [Theory] // legal — 波次盾接线（E3 公式提前接线验证）：10+8×波（v3.2 §6）
    [InlineData(15, 15, 130)] // 10+8×15
    [InlineData(16, 15, 138)]
    [InlineData(20, 15, 170)]
    [InlineData(14, 15, 0)]   // 起始波前不回灌（E3 不向 E2 泄漏）
    public void Wave_shield_wiring_follows_ten_plus_eight_per_wave(int wave, int startWave, int expected)
    {
        Assert.Equal(expected, Shield.At(wave, startWave)); // 公式本体（Balance 冻结）

        WaveSpawner spawner = NewSpawner(NewConfig(shieldStartWave: startWave));
        spawner.BeginWave(wave, startTick: 0);
        IReadOnlyList<Unit> spawned = spawner.Poll(0);

        Assert.All(spawned, u => Assert.Equal(expected, u.Shield));
    }

    [Fact] // legal — 个体盾框架位：合成带 shield 行的目录实刷后单位带盾
    public void Individual_shield_slot_wires_through_the_catalog()
    {
        EnemySpecCatalog catalog = EnemySpecCatalog.Parse(SyntheticCatalogs.ShieldedRoster);
        var spawner = new WaveSpawner(
            catalog,
            new WaveSpawnConfig(
                BaseCount: 4, BeginWave: 5, Spacing: 5, Scaling: 2.0, ThreatLevel: 0.75,
                RegularRoster: new[] { "t:enemy-shielded" },
                EntryCells: new[] { new CellCoord(0, 0) }),
            new SplitMixRng(3),
            new FixedClock());

        spawner.BeginWave(10, startTick: 0);
        IReadOnlyList<Unit> spawned = spawner.Poll(0);

        Assert.NotEmpty(spawned);
        Assert.All(spawned, u => Assert.Equal(64, u.Shield)); // 合成行 shield:64
    }
}

/// <summary>合成目录夹具（方法体外构造字面 JSON——T4 纪律）。</summary>
internal static class SyntheticCatalogs
{
    public static string ShieldedRoster =>
        "{\"content_type\":\"Enemy\",\"tick_rate_hz\":30,\"cell_size_px\":32,\"items\":[{"
        + "\"id\":\"t:enemy-shielded\",\"content_type\":\"Enemy\",\"sp_id\":\"t_shielded\",\"name_zh\":\"盾兵\","
        + "\"hp\":100,\"armor\":2,\"speed_px_per_tick\":1.0,\"shield\":64}]}";
}
