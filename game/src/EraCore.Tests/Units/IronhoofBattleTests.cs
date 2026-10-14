using EraCore.Agent.Actions;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using EraCore.Tests.Agent.Actions;
using EraCore.Units;
using EraCore.World;
using Xunit;

namespace EraCore.Tests.Units;

// 铁蹄可战（T-066 集成验收）：E2 敌表+精英线+战鼓+冲撞+践踏全链——60 s
// 模拟窗口（30 Hz=1800 ticks）内战斗必有结果；落石台常量取自 era1
// towers.json（CS-4.4：E2 塔表为 T-065 交付物，此处火力源仍数据驱动）。
// [C-10] [F-033] [F-031] [F-039]
public class IronhoofBattleTests
{
    private const int CellSize = 32;
    private const int MapWidth = 12;
    private const int MapHeight = 34;
    private const int PathCol = 6;
    private const long WindowTicks = 1800; // 完整 60 s 模拟窗口（30 Hz）

    private sealed record BattleOutcome(
        bool Killed,
        bool ArrivedAtBase,
        long EndTick,
        int ShotsFired,
        int TowersDestroyed,
        int TowerCount,
        int TotalDamage,
        int BossHpLost,
        int TramplePulses,
        int ChargeGridTicks,
        int ChargeRams,
        long StateHash);

    private static (Unit Boss, List<Unit> Escorts) SpawnBossWithEscorts(int escortCount)
    {
        var spawner = new WaveSpawner(
            UnitsContent.LoadEra2EnemyCatalog(),
            new WaveSpawnConfig(
                BaseCount: 10, BeginWave: 5, Spacing: 5, Scaling: 2.0, ThreatLevel: 0.75,
                RegularRoster: new[] { "era2:enemy-skirmisher" },
                BossWave: 20, BossEnemyId: "era2:enemy-boss",
                BatchesPerWave: 1,
                EntryCells: new[] { new CellCoord(PathCol, 0) },
                Trample: new TrampleProfile(Damage: 100, PeriodTicks: 45)), // [NEW-原型校准]（测试侧配置）
            new SplitMixRng(11),
            new FixedClock());

        spawner.BeginWave(20, startTick: 0);
        Unit boss = spawner.Poll(0).Single(u => u.IsBoss);
        Assert.Equal(6000, boss.MaxHp); // 4000 × 1.5（v3.2 §6 Boss 状态；威胁 0.75 HP 乘子 1.0）
        Assert.Equal(10, boss.Armor);
        Assert.Equal(600, boss.ChargeIntervalTicks); // 每 20 s（SP-3b L121）
        // ceil(150 × 1.3)=195：乘积在 double 里恰舍到 195（Boss 状态 ×1.3 入冲撞伤）
        Assert.Equal(195, boss.ChargeDamage);
        Assert.Equal(96, boss.WarDrumsRadiusPx);    // 3 格（SP-3b L120）

        var escorts = new List<Unit>();
        for (int i = 0; i < escortCount; i++)
        {
            // 两侧贴身护卫（±1 列）：同排出发，全程在彼此与 Boss 的鼓界内一段时间
            int col = i % 2 == 0 ? PathCol - 1 : PathCol + 1;
            var escort = new Unit(
                new EntityId(900 + i), "era2:enemy-centurion",
                col * CellSize + CellSize / 2f, CellSize / 2f,
                900, 6, 0.96f);
            escort.SetWarDrums(96, 15); // SP-3b L120：半径 3 格、友军 +15%
            escort.MarkSpawned(0);
            escorts.Add(escort);
        }

        return (boss, escorts);
    }

    /// <summary>
    /// Runs the 30 Hz loop: aura pass → speed resolve → movement → tower fire →
    /// trample/charge pulses, until the boss dies, arrives at (6,33), or the
    /// 60 s window ends. Towers come from <see cref="UnitsContent.LoadRockfallTower"/>.
    /// </summary>
    private static BattleOutcome RunBattle(
        IReadOnlyList<(int Col, int Row)> towerPlacements,
        int escortCount = 0)
    {
        var world = new GridWorld(MapWidth, MapHeight, new SplitMixRng(5), forestRatio: 0, rockRatio: 0, waterRatio: 0);
        FlowField field = FlowField.BuildComplete(world, new CellCoord(PathCol, MapHeight - 1));
        var movement = new UnitMovement(() => field, CellSize);

        (Unit boss, List<Unit> escorts) = SpawnBossWithEscorts(escortCount);
        var units = new List<Unit> { boss };
        units.AddRange(escorts);

        var stats = UnitsContent.LoadRockfallTower();
        var towers = new List<TestTower>();
        int index = 0;
        foreach ((int col, int row) in towerPlacements)
        {
            var tower = new TestTower(col * CellSize + 16f, row * CellSize + 16f, stats)
            {
                NextFireTick = index % stats.ReloadTicks, // 供弹错相：持续火力而非齐射浪涌
            };
            towers.Add(tower);
            index++;
        }

        int destroyed = 0;
        int pulses = 0;
        int chargeGrid = 0;
        int rams = 0;
        for (long tick = 1; tick <= WindowTicks; tick++)
        {
            UnitAuras.ApplyWarDrums(units);
            foreach (Unit unit in units)
            {
                unit.ResolveSpeedEffects(tick);
                unit.TickShield(tick);
                movement.Step(unit);
            }

            foreach (TestTower tower in towers)
            {
                tower.FireAt(units, tick);
                if (!tower.Alive && tower.DestroyedTick == -1)
                {
                    tower.MarkDestroyed(tick);
                    destroyed++;
                }
            }

            if (movement.PulseTrample(boss, towers, tick).Count > 0)
            {
                pulses++;
            }

            if (movement.PulseCharge(boss, towers, tick).Count > 0)
            {
                rams++;
            }

            // 冲撞节拍格点计数（与 PulseCharge 同判据：phase>0 且整除 600）
            if (boss.Alive && tick - boss.SpawnTick > 0 && (tick - boss.SpawnTick) % boss.ChargeIntervalTicks == 0)
            {
                chargeGrid++;
            }

            if (!boss.Alive || boss.ArrivedAtTarget)
            {
                return Outcome(tick);
            }
        }

        return Outcome(WindowTicks);

        BattleOutcome Outcome(long endTick) => new(
            Killed: !boss.Alive,
            ArrivedAtBase: boss.ArrivedAtTarget,
            EndTick: endTick,
            ShotsFired: towers.Sum(t => t.ShotsFired),
            TowersDestroyed: destroyed,
            TowerCount: towers.Count,
            TotalDamage: towers.Sum(t => t.DamageDealt),
            BossHpLost: boss.MaxHp - boss.Hp,
            TramplePulses: pulses,
            ChargeGridTicks: chargeGrid,
            ChargeRams: rams,
            StateHash: UnitStateHash.Compute(units));
    }

    private static List<(int Col, int Row)> FlankingWall(int fromRow, int toRow) =>
        (from col in new[] { PathCol - 2, PathCol - 1, PathCol + 1, PathCol + 2 }
         from row in Enumerable.Range(fromRow, toRow - fromRow + 1)
         select (col, row)).ToList();

    [Fact] // legal — 火力墙击杀线：铁蹄 60 s 内被击杀，射弹与伤害守恒
    public void Ironhoof_dies_inside_the_rockfall_wall_within_sixty_seconds()
    {
        var stats = UnitsContent.LoadRockfallTower();
        var outcome = RunBattle(FlankingWall(2, 31));

        // 手算（era1 towers.json 落石台 d=60、reload=90、range=4 格；铁蹄甲 10）：
        // 单发有效伤 = max(60−10, ceil(10%×60)) = 50；6000 HP 恰需 120 发；
        // 120 塔（4 列×30 行）× 每 90 tick 一发 → 理论最快 3 个供弹周期。
        int effPerShot = Math.Max(60 - 10, (60 + 9) / 10);
        Assert.Equal(50, effPerShot);
        int minShots = (int)Math.Ceiling(6000.0 / effPerShot);
        Assert.Equal(120, minShots);
        long theoreticalFastestTicks = (long)Math.Ceiling(6000.0 / (outcome.TowerCount * effPerShot) * 30);
        long expectedArrival = (long)Math.Ceiling((33.0 * CellSize - 16.0) / 0.64);

        Assert.True(outcome.Killed, $"铁蹄未被击杀（终局={outcome.ArrivedAtBase}, 末刻={outcome.EndTick}）");
        Assert.False(outcome.ArrivedAtBase);
        Assert.True(outcome.EndTick >= theoreticalFastestTicks, "快过理论最快供弹即守恒破坏");
        Assert.True(outcome.EndTick < expectedArrival, "击杀必须发生在推进到基地之前");
        Assert.InRange(outcome.EndTick, theoreticalFastestTicks, WindowTicks); // ≤ 60 s
        Assert.True(outcome.ShotsFired >= minShots, $"射弹 {outcome.ShotsFired} 发不足以造成 6000 伤害");

        int maxShots = outcome.TowerCount * (int)(WindowTicks / stats.ReloadTicks) + outcome.TowerCount;
        Assert.True(outcome.ShotsFired <= maxShots, "射弹数超过供弹物理上限");

        // 伤害守恒：d=60、甲=10 全程固定 ⇒ 每发恒 50；6000/50=120 恰整除 ⇒
        // 总伤害恰 6000、无溢杀（区别于 E1 踏群者战：此处整除无余数）。
        Assert.Equal(6000, outcome.TotalDamage);
        Assert.Equal(6000, outcome.BossHpLost);
        Assert.Equal(120, outcome.ShotsFired);
        Assert.True(outcome.TramplePulses >= 1, "践踏必须参战（首个 45 tick 节拍命中贴廊塔）");
        // 注：毁塔面由冲撞专项测试钉（本战 120 塔齐射在 ~90 tick 内终局，
        // 践踏的第二记节拍未及落下——战斗结束先于塔倒）。
    }

    [Fact] // legal — 零塔对照：铁蹄无伤推进，到达时刻=基速+冲撞提速手算值
    public void Ironhoof_walks_unharmed_to_the_base_with_zero_towers()
    {
        var outcome = RunBattle(new List<(int, int)>(0));

        // 手算：基速 0.64 px/tick，1040 px 纯走需 1625 tick；20 s 冲撞节拍自带
        // 90 tick×+50% 提速两窗（600~690、1200~1290）：
        // 0.64×1200+0.32×90=796.8 → +0.96×90=86.4 → 883.2 px @1290 → 余 156.8 px ÷0.64=245
        // → 预计 1535（±4=格心 Snap 取整容差）。
        long expectedArrival = 1200 + 90 + (long)Math.Ceiling((1040.0 - 883.2) / 0.64);
        Assert.InRange(expectedArrival, 1530, 1540);
        Assert.False(outcome.Killed, "零火力下铁蹄必存活至到达");
        Assert.True(outcome.ArrivedAtBase, "基地必须被突破（零塔时唯一结局）");
        Assert.InRange(outcome.EndTick, expectedArrival - 4, expectedArrival + 4);
        Assert.Equal(0, outcome.BossHpLost);
        Assert.True(outcome.ChargeGridTicks > 0, "冲撞 20 s 节拍格点应在窗口内出现（600/1200）");
    }

    [Fact] // legal — 战鼓参战：护卫在鼓界内时铁蹄 +15%、推进快于无鼓对照
    public void War_drums_escort_accelerates_the_ironhoof()
    {
        // 有鼓组：两护卫贴身（±1 列同排），鼓界覆盖铁蹄
        var withDrums = SpawnBossWithEscorts(escortCount: 2);
        var world = new GridWorld(MapWidth, MapHeight, new SplitMixRng(5), 0, 0, 0);
        FlowField field = FlowField.BuildComplete(world, new CellCoord(PathCol, MapHeight - 1));
        var movement = new UnitMovement(() => field, CellSize);
        var units = new List<Unit> { withDrums.Boss };
        units.AddRange(withDrums.Escorts);

        float yAt100With = 0;
        int auraAt100 = 0;
        for (long tick = 1; tick <= 100; tick++)
        {
            UnitAuras.ApplyWarDrums(units);
            foreach (Unit unit in units)
            {
                unit.ResolveSpeedEffects(tick);
                movement.Step(unit);
            }

            if (tick == 100)
            {
                withDrums.Boss.ResolveSpeedEffects(tick);
                auraAt100 = withDrums.Boss.ResolvedSpeedBonusPercent;
                yAt100With = withDrums.Boss.Y;
            }
        }

        Assert.Equal(15, auraAt100); // 鼓界内 +15%（SP-3b L120）
        Assert.Equal(withDrums.Boss.SpeedPxPerTick * 1.15f, withDrums.Boss.EffectiveSpeedPxPerTick, 5);

        // 无鼓对照：同种子同走廊，铁蹄原速
        var without = SpawnBossWithEscorts(escortCount: 0);
        for (long tick = 1; tick <= 100; tick++)
        {
            without.Boss.ResolveSpeedEffects(tick);
            movement.Step(without.Boss);
        }

        // 100 tick 分离：0.64×1.15−0.64=0.096 px/tick × 100 ≈ 9.6 px（含取整路径）
        Assert.True(yAt100With > without.Boss.Y + 5,
            $"战鼓必须实际加速推进（有鼓 {yAt100With} vs 无鼓 {without.Boss.Y}）");
    }

    [Fact] // legal — 冲撞参战：20 s 节拍上撞击 2 格内建筑并进入冲锋提速
    public void Charge_rams_a_tower_on_the_twenty_second_grid_and_bursts()
    {
        var (boss, _) = SpawnBossWithEscorts(0);
        var stats = UnitsContent.LoadRockfallTower();
        // 手算：0.64 px/tick 自 y=16 出发——tick 600 → y≈400（行 12 中心）、
        // tick 1200 → y≈784（行 24 中心）；贴廊列 5（横距 32 px ≤ 触及 64 px）。
        var towers = new List<TestTower>
        {
            new((PathCol - 1) * CellSize + 16f, 12 * CellSize + 16f, stats),
            new((PathCol - 1) * CellSize + 16f, 24 * CellSize + 16f, stats),
        };
        var world = new GridWorld(MapWidth, MapHeight, new SplitMixRng(5), 0, 0, 0);
        FlowField field = FlowField.BuildComplete(world, new CellCoord(PathCol, MapHeight - 1));
        var movement = new UnitMovement(() => field, CellSize);

        bool burstSeen = false;
        int rams = 0;
        for (long tick = 1; tick <= WindowTicks && boss.Alive; tick++)
        {
            boss.ResolveSpeedEffects(tick);
            movement.Step(boss);
            if (movement.PulseCharge(boss, towers, tick).Count > 0)
            {
                rams++;
            }

            if (tick == 645)
            {
                boss.ResolveSpeedEffects(tick);
                burstSeen = tick < boss.ChargeUntilTick; // 600+90=690 窗口内
            }
        }

        Assert.Equal(2, rams); // tick 600 与 1200 两次节拍均命中各自塔
        Assert.True(burstSeen, "冲撞后 90 tick 冲锋提速窗口必须可观察");
        Assert.All(towers, t => Assert.False(t.Alive)); // 195 > 150 HP：一次冲撞即毁塔
    }

    [Fact] // legal — HeroOrder 走注册表参战：E2 英雄突刺在 Boss 战中生效（−4 甲 10 s）
    public void Registry_skill_order_lands_the_thrust_on_the_boss()
    {
        var (boss, _) = SpawnBossWithEscorts(0);
        Hero hero = new(
            UnitsContent.LoadHeroCatalog().Get("era2:hero-spearwarden"), new EntityId(700), 500, 500);
        var enemies = new List<Unit> { boss };
        HeroOrderAction handler = new(
            hero: () => hero,
            advanceCell: () => new CellCoord(PathCol, 1),
            enemies: () => enemies,
            cellSizePx: CellSize)
        {
            TickSource = () => 1,
        };
        CapturingLogSink sink = new();
        var registry = new ActionRegistry(
            sink, worldView: null, eraOrdinalProvider: () => EraOrdinals.E2, heroOrder: handler);

        // 英雄贴到 Boss 2 格内（64 px）再下令
        hero.MoveTo(boss.X + 32, boss.Y);
        ActionOutcome outcome = registry.Invoke(HeroOrderJson.Skill());

        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        Assert.Equal(HeroSkillStatus.Fired, ((HeroOrderAck)outcome.Payload!).SkillStatus);
        Assert.Equal(4, boss.ArmorReduction);
        Assert.Equal(6, boss.EffectiveArmorAt(1));    // 10−4（先减甲）
        Assert.Equal(10, boss.EffectiveArmorAt(301)); // 10 s 后恢复
        // 落伤经减甲后的护甲：DirectInt(200, 6)=194 → 6000−194
        Assert.Equal(6000 - 194, boss.Hp);
    }

    [Fact] // legal — 确定性：整场战斗重放一致（结果记录与状态哈希逐位相等）
    public void The_battle_replays_identically()
    {
        var first = RunBattle(FlankingWall(2, 31), escortCount: 1);
        var second = RunBattle(FlankingWall(2, 31), escortCount: 1);

        Assert.Equal(first, second); // 记录全等（含 StateHash）
        Assert.True(first.Killed);
    }
}
