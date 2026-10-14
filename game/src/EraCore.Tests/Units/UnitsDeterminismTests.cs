using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using EraCore.Units;
using EraCore.World;
using Xunit;

namespace EraCore.Tests.Units;

// [F-030] 确定性（C-10 E1 薄版，T-039）：同 seed + 同波参数 → 入场序列与
// 移动轨迹哈希逐点一致（03-decomposition §1.2 回放要求在单位系统的首证，
// C-12/T-040 编排落地前的泳道内证明）。
public class UnitsDeterminismTests
{
    /// <summary>
    /// Runs one seeded 30-tick-per-second simulation over a real GridWorld +
    /// FlowField: autonomous waves spawn, units walk the gradient, hashes are
    /// sampled every 30 ticks. Returns (spawn log, hash timeline).
    /// </summary>
    private static (List<string> SpawnLog, List<long> HashTimeline) RunSimulation(long seed, long ticks)
    {
        var world = new GridWorld(16, 24, new SplitMixRng(99), forestRatio: 0.1, rockRatio: 0.05, waterRatio: 0.0);
        FlowField field = FlowField.BuildComplete(world, new CellCoord(8, 23));
        var movement = new UnitMovement(() => field, 32);

        var clock = new FixedClock();
        var spawner = new WaveSpawner(
            UnitsContent.LoadEnemyCatalog(),
            new WaveSpawnConfig(
                // t=1.2（F.2 带上沿）：间隔乘子 0.6 → 72 s/波，宽限后 15k tick 内推进 ~6 波
                BaseCount: 10, BeginWave: 5, Spacing: 5, Scaling: 2.0, ThreatLevel: 1.2,
                RegularRoster: new[] { "era1:enemy-beastling", "era1:enemy-tribesman", "era1:enemy-hunter" },
                EliteLineEnabled: true, EliteEnemyId: "era1:enemy-boar",
                BatchesPerWave: 4,
                EntryCells: new[] { new CellCoord(8, 0), new CellCoord(6, 0), new CellCoord(10, 0), new CellCoord(2, 1) },
                Trample: new TrampleProfile(60, 45)),
            new SplitMixRng(seed),
            clock);

        var units = new List<Unit>();
        var spawnLog = new List<string>();
        var timeline = new List<long>();
        for (long tick = 0; tick <= ticks; tick++)
        {
            clock.Set(tick);
            foreach (Unit unit in spawner.Advance())
            {
                units.Add(unit);
                spawnLog.Add($"{tick}|{unit.Id.Value}|{unit.ContentId}|{unit.X:F3}|{unit.Y:F3}|{unit.MaxHp}");
            }

            movement.StepAll(units);
            if (tick % 30 == 0)
            {
                timeline.Add(UnitStateHash.Compute(units));
            }
        }

        return (spawnLog, timeline);
    }

    [Fact] // legal — 同 seed 同波参数：入场序列逐条一致 + 轨迹哈希时间线逐点一致
    public void Same_seed_replays_identical_spawn_sequence_and_trajectory()
    {
        const long ticks = 15_000; // ~500 s：覆盖首波宽限 + 多波推进

        var first = RunSimulation(seed: 42, ticks);
        var second = RunSimulation(seed: 42, ticks);

        Assert.True(first.SpawnLog.Count > 50, $"样本不足：{first.SpawnLog.Count} 条出生记录");
        Assert.Equal(first.SpawnLog, second.SpawnLog);            // 入场序列（时刻/身份/物种/落点/HP）
        Assert.Equal(first.HashTimeline, second.HashTimeline);    // 移动轨迹哈希时间线
    }

    [Fact] // legal — 换 seed：入口格选择流不同 → 轨迹分叉（哈希不一致）
    public void Different_seed_diverges_the_trajectory()
    {
        const long ticks = 15_000;

        var first = RunSimulation(seed: 42, ticks);
        var third = RunSimulation(seed: 4242, ticks);

        Assert.Equal(first.SpawnLog.Count, third.SpawnLog.Count); // 数量仍由公式决定（seed 无关）
        Assert.NotEqual(first.HashTimeline, third.HashTimeline);  // 落点/轨迹不同
    }

    [Fact] // legal — IRng 端口语义：Reseed 后序列与新建同 seed 实例一致（回放要件）
    public void Reseed_restores_the_exact_draw_sequence()
    {
        var stream = new SplitMixRng(7);
        var reference = Enumerable.Range(0, 16)
            .Select(_ => (Draw: stream.Next(0, 4), Unit: stream.NextDouble()))
            .ToList();

        var replayStream = new SplitMixRng(999);
        replayStream.Reseed(7);
        var replay = Enumerable.Range(0, 16)
            .Select(_ => (Draw: replayStream.Next(0, 4), Unit: replayStream.NextDouble()))
            .ToList();

        Assert.Equal(reference, replay);
        // 判别力自查：篡改一条记录后必不相等（防恒真空比较）
        var tampered = reference.ToList();
        tampered[3] = (tampered[3].Draw, tampered[3].Unit + 1.0);
        Assert.NotEqual(reference, tampered);
    }

    // ------------------------------------------------ T-066：E2+英雄+护盾的确定性面

    /// <summary>
    /// E2 面：era2 敌表 + 精英线（攻城锤）+ 战鼓光环 + 护盾再生 + 英雄（战吼
    /// 减速/受令移动）同场——含速度通道的整链在重放下哈希逐点一致。
    /// </summary>
    private static (List<string> SpawnLog, List<long> HashTimeline) RunEra2Simulation(long seed, long ticks)
    {
        var world = new GridWorld(16, 24, new SplitMixRng(99), forestRatio: 0.1, rockRatio: 0.05, waterRatio: 0.0);
        FlowField field = FlowField.BuildComplete(world, new CellCoord(8, 23));
        var movement = new UnitMovement(() => field, 32);

        var clock = new FixedClock();
        var spawner = new WaveSpawner(
            UnitsContent.LoadEra2EnemyCatalog(),
            new WaveSpawnConfig(
                // t=1.2（F.2 带上沿）：间隔乘子 0.6 → 72 s/波（与 E1 面同节奏，15k tick 推进 ~6 波）
                BaseCount: 10, BeginWave: 5, Spacing: 5, Scaling: 2.0, ThreatLevel: 1.2,
                RegularRoster: new[] { "era2:enemy-skirmisher", "era2:enemy-warrior", "era2:enemy-centurion" },
                EliteLineEnabled: true, EliteEnemyId: "era2:enemy-ram",
                BatchesPerWave: 4,
                EntryCells: new[] { new CellCoord(8, 0), new CellCoord(6, 0), new CellCoord(10, 0) },
                ShieldStartWave: 3), // E3 公式提前接线：第 3 波起 10+8×波（含破盾再生时序）
            new SplitMixRng(seed),
            clock);

        Hero hero = new(
            UnitsContent.LoadHeroCatalog().Get("era1:hero-firstfire"), new EntityId(501), 8 * 32 + 16f, 22 * 32 + 16f);
        var enemiesOfHero = new List<Unit>();

        var units = new List<Unit>();
        var spawnLog = new List<string>();
        var timeline = new List<long>();
        for (long tick = 0; tick <= ticks; tick++)
        {
            clock.Set(tick);
            foreach (Unit unit in spawner.Advance())
            {
                units.Add(unit);
                enemiesOfHero.Add(unit);
                spawnLog.Add($"{tick}|{unit.Id.Value}|{unit.ContentId}|{unit.X:F3}|{unit.Y:F3}|{unit.MaxHp}|{unit.Shield}");
            }

            UnitAuras.ApplyWarDrums(units); // 百夫长/铁蹄战鼓（era2 敌表自帶）
            foreach (Unit unit in units)
            {
                unit.ResolveSpeedEffects(tick);
                unit.TickShield(tick); // 破盾 5 s 后 20/s + 穿甲打断时序入哈希面
                movement.Step(unit);
            }

            hero.Tick(tick);
            if (tick % 450 == 0)
            {
                hero.TryUseSkill(tick, enemiesOfHero, 32); // 战吼每 15 s 一档（45 s 冷却内多数 No-op）
                hero.OrderMove(new CellCoord(8, (int)Math.Min(20, tick / 150)), 32); // 受令推进
            }

            if (hero.HasOrder && hero.Alive)
            {
                movement.StepOrdered(hero, hero.OrderedX!.Value, hero.OrderedY!.Value);
            }

            if (tick % 30 == 0)
            {
                timeline.Add(UnitStateHash.Compute(units) * 31 + hero.Hp); // 敌群+英雄联合哈希
            }
        }

        return (spawnLog, timeline);
    }

    [Fact] // legal — E2 全链重放：同 seed 入场/护盾/轨迹/英雄联合哈希逐点一致
    public void Era2_full_chain_replays_identically_with_hero_and_shields()
    {
        const long ticks = 15_000; // ~500 s：覆盖宽限 + ~6 波 + 破盾再生窗

        var first = RunEra2Simulation(seed: 77, ticks);
        var second = RunEra2Simulation(seed: 77, ticks);

        Assert.True(first.SpawnLog.Count > 40, $"样本不足：{first.SpawnLog.Count} 条出生记录");
        Assert.Equal(first.SpawnLog, second.SpawnLog);
        Assert.Equal(first.HashTimeline, second.HashTimeline);
    }

    [Fact] // legal — 判别力自查：换 seed 必分叉（速度通道/护盾并非恒定量的旁证）
    public void Era2_chain_diverges_across_seeds()
    {
        const long ticks = 15_000;

        var first = RunEra2Simulation(seed: 77, ticks);
        var third = RunEra2Simulation(seed: 777, ticks);

        Assert.Equal(first.SpawnLog.Count, third.SpawnLog.Count);
        Assert.NotEqual(first.HashTimeline, third.HashTimeline);
    }
}
