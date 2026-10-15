using EraCore.Balance;
using EraCore.Contracts.Foundation;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Units;

// [F-032] 护盾 E3 实装（T-114，v3.2 §6 L84「护盾 E3 起（破盾 5s 后 20/s 再生，
// 穿甲打断）」）：波次盾公式 10+8×波 经 WaveSpawnConfig.ShieldStartWave=31
// （E2 金样表 shield_start_wave 记录值=首个 E3 波）接入实刷单位；个体盾与
// 波盾取最大不叠加（WaveSpawner.CreateUnit T-114 裁定①②）；破盾再生/穿甲
// 打断时序由 ShieldStateTests（T-066 冻结域）覆盖，此处钉波次维度的三裁定。
public class Era3WaveShieldTests
{
    private const int E3ShieldStartWave = 31; // E2 金样表 shield_start_wave 记录值（首个 E3 波）

    private static WaveSpawnConfig NewConfig(
        int bossWave = 45, // 默认脱离 31~50 表段：公式/叠加行只看常规刷（Boss 波=Boss 单独批次）
        int? shieldStartWave = E3ShieldStartWave,
        string rosterEnemy = "era3:enemy-line-infantry") =>
        new(
            BaseCount: 10, BeginWave: 5, Spacing: 5, Scaling: 2.0, ThreatLevel: 0.75,
            RegularRoster: new[] { rosterEnemy },
            BossWave: bossWave, BossEnemyId: "era3:enemy-boss",
            BatchesPerWave: 1, // 单批全量入场：Poll(0) 即足额（批量节奏由 WaveSpawnerTests 覆盖）
            EntryCells: new[] { new CellCoord(6, 0) },
            ShieldStartWave: shieldStartWave);

    private static WaveSpawner NewSpawner(WaveSpawnConfig config) =>
        new(UnitsContent.LoadEra3EnemyCatalog(), config, new SplitMixRng(11), new FixedClock());

    // ---- 裁定①：波次盾公式（E3 起 10+8×波）--------------------------------

    [Theory] // legal — 表驱动：波 31/40/50 的实刷单位群体携带 10+8×波（任务卡锚波）
    [InlineData(31, 258)] // 10+8×31：首个 E3 波（E2 金样表 shield_start_wave 记录值）
    [InlineData(40, 330)] // 10+8×40：E3 Boss 波
    [InlineData(50, 410)] // 10+8×50：表外延伸（公式不封顶）
    [InlineData(32, 266)] // 次波：+8/波 斜率在相邻波亦成立
    public void E3_waves_spawn_with_the_formula_shield(int wave, int expected)
    {
        Assert.Equal(expected, Shield.At(wave, E3ShieldStartWave)); // 公式本体（Balance 冻结）

        WaveSpawner spawner = NewSpawner(NewConfig());
        spawner.BeginWave(wave, startTick: 0);
        IReadOnlyList<Unit> spawned = spawner.Poll(0);

        Assert.NotEmpty(spawned);
        Assert.All(spawned.Where(u => !u.IsBoss), u => Assert.Equal(expected, u.Shield));
    }

    [Theory] // boundary — 起始波前不回灌：E2 段（波 ≤30）与关闭位（null）恒 0 盾
    [InlineData(30)]
    [InlineData(29)]
    public void Pre_E3_waves_stay_unshielded(int wave)
    {
        WaveSpawner spawner = NewSpawner(NewConfig());
        spawner.BeginWave(wave, startTick: 0);
        IReadOnlyList<Unit> spawned = spawner.Poll(0);

        Assert.NotEmpty(spawned);
        Assert.All(spawned, u => Assert.Equal(0, u.Shield)); // 个体盾=0 的常规行：波前无盾
    }

    [Fact] // boundary — ShieldStartWave=null 显式关闸：E1/E2 语义（护盾 E3 起，不向前代泄漏）
    public void Null_start_wave_disables_the_wave_shield_entirely()
    {
        WaveSpawner spawner = NewSpawner(NewConfig(shieldStartWave: null));
        spawner.BeginWave(40, startTick: 0);
        IReadOnlyList<Unit> spawned = spawner.Poll(0).Where(u => !u.IsBoss).ToList();

        Assert.NotEmpty(spawned);
        Assert.All(spawned, u => Assert.Equal(0, u.Shield));
    }

    // ---- 裁定②：个体盾与波盾取最大不叠加 -----------------------------------

    [Theory] // legal — 叠加裁定表：max(波盾, 个体盾)，非求和（WaveSpawner T-114 裁定②）
    [InlineData(30, 240)]  // 波盾 0：纯个体盾（铁甲卫兵 spec shield=240）
    [InlineData(31, 258)]  // max(258, 240)=258：波盾反超的首波
    [InlineData(40, 330)]  // max(330, 240)=330
    public void Individual_and_wave_shields_take_the_maximum_not_the_sum(int wave, int expected)
    {
        WaveSpawner spawner = NewSpawner(NewConfig(rosterEnemy: "era3:enemy-ironclad-guard"));
        spawner.BeginWave(wave, startTick: 0);
        IReadOnlyList<Unit> spawned = spawner.Poll(0);

        Assert.NotEmpty(spawned);
        Assert.All(spawned, u => Assert.Equal(expected, u.Shield));
        // 求和口径的反证锚：wave 31 若叠加将得 498（258+240）——裁定明确排除
        if (wave == 31)
        {
            Assert.NotEqual(258 + 240, spawned[0].Shield);
        }
    }

    [Fact] // legal — Boss 个体盾 600 压过任意 E3 波盾（堡垒号数据位 T-112）
    public void Fortress_keeps_its_individual_six_hundred_over_the_wave_shield()
    {
        WaveSpawner spawner = NewSpawner(NewConfig(bossWave: 40));
        spawner.BeginWave(40, startTick: 0);
        Unit boss = spawner.Poll(0).Single(u => u.IsBoss);

        Assert.Equal(600, boss.Shield); // max(10+8×40=330, spec 600)=600
        Assert.Equal(13500, boss.MaxHp); // 9000 × 1.5（v3.2 §6 Boss 状态；威胁 0.75 HP 乘子 1.0）
        Assert.Equal(16, boss.Armor);
    }

    // ---- 裁定③：波开始刷新语义=入场满盾，不追溯刷新存活者 -------------------

    [Fact] // legal — 第 31 波存活者的盾容量冻结在其入场波：第 32 波开始不追溯抬升
    public void Later_waves_do_not_retro_refresh_earlier_survivors()
    {
        WaveSpawner spawner = NewSpawner(NewConfig());
        spawner.BeginWave(31, startTick: 0);
        IReadOnlyList<Unit> wave31 = spawner.Poll(0);
        Assert.All(wave31, u => Assert.Equal(258, u.Shield));

        spawner.BeginWave(32, startTick: 1500); // 下一波按调度开始（任意起点，语义同）
        IReadOnlyList<Unit> wave32 = spawner.Poll(1500);

        Assert.All(wave32.Where(u => !u.IsBoss), u => Assert.Equal(266, u.Shield)); // 新入场=新波值
        Assert.All(wave31, u => Assert.Equal(258, u.Shield)); // 存活者容量不追溯刷新（未受损=满 258）
    }
}
