using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using EraCore.Units;
using EraCore.World;
using Xunit;

namespace EraCore.Tests.Units;

// 踏群者可战（T-039 集成验收）：GridWorld + FlowField +
// 木箭塔开火循环（towers.json 常量手搓，CS-4.4）+ 第 20 波 Boss
// （×1.5HP/×1.3 伤）→ 60 s 窗口内战斗必有结果（击杀或推进到基地），
// 且结果时刻与手算数量级一致。
// [C-10] [F-033]
public class BossBattleIntegrationTests
{
    private const int CellSize = 32;
    private const int MapWidth = 12;
    private const int MapHeight = 34;   // 进攻走廊长 ~32 格：Boss 全程 ~46 s
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
        int TramplePulses);

    /// <summary>
    /// Builds the corridor world, spawns the wave-20 stomper at (6,0) and
    /// runs the 30 Hz loop: movement → tower fire → trample pulses, until the
    /// boss dies, arrives at the base cell (6,33), or the 60 s window ends.
    /// Observability (batch2 review F2): <see cref="BattleOutcome.TotalDamage"/>
    /// sums every settled <see cref="Unit.ApplyDamage"/> return (damage
    /// conservation), BossHpLost is the boss-side view (MaxHp − Hp), and
    /// TramplePulses counts the boss's pulse-grid ticks (tick − SpawnTick on
    /// the period grid, phase &gt; 0 — the same grid
    /// <see cref="UnitMovement.PulseTrample"/> fires on; counted test-side
    /// because the engine short-circuits on an empty target list, which is
    /// exactly the zero-tower control case).
    /// </summary>
    private static BattleOutcome RunBattle(IReadOnlyList<(int Col, int Row)> towerPlacements)
    {
        // 全平原走廊（比例全 0）：地形不挡道，唯一变量是火力与践踏
        var world = new GridWorld(MapWidth, MapHeight, new SplitMixRng(5), forestRatio: 0, rockRatio: 0, waterRatio: 0);
        FlowField field = FlowField.BuildComplete(world, new CellCoord(PathCol, MapHeight - 1));
        var movement = new UnitMovement(() => field, CellSize);

        var clock = new FixedClock();
        var spawner = new WaveSpawner(
            UnitsContent.LoadEnemyCatalog(),
            new WaveSpawnConfig(
                BaseCount: 10, BeginWave: 5, Spacing: 5, Scaling: 2.0, ThreatLevel: 0.75,
                RegularRoster: new[] { "era1:enemy-beastling" },
                BossWave: 20, BossEnemyId: "era1:enemy-boss",
                BatchesPerWave: 4,
                EntryCells: new[] { new CellCoord(PathCol, 0) },
                Trample: new TrampleProfile(Damage: 60, PeriodTicks: 45)), // [NEW-原型校准]
            new SplitMixRng(11),
            clock);

        spawner.BeginWave(20, startTick: 0); // 终局关第 20 波：踏群者
        Unit boss = spawner.Poll(0).Single(u => u.IsBoss);
        Assert.Equal(3750, boss.MaxHp); // 2500 × 1.5（×1.3 伤已入 TrampleDamage=78）
        Assert.Equal(0, boss.SpawnTick); // 践踏相位基准（TramplePulses 计数依赖）

        var stats = UnitsContent.LoadWoodArrowTower();
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

        var units = new List<Unit> { boss };
        int destroyed = 0;
        int pulses = 0;
        for (long tick = 1; tick <= WindowTicks; tick++)
        {
            clock.Set(tick);
            movement.Step(boss);
            foreach (TestTower tower in towers)
            {
                tower.FireAt(units, tick);
                if (!tower.Alive && tower.DestroyedTick == -1)
                {
                    tower.MarkDestroyed(tick);
                    destroyed++;
                }
            }

            movement.PulseTrample(boss, towers, tick);
            // 践踏节拍格点计数（与 PulseTrample 的相位判据同式：phase>0 且整除周期；
            // 引擎对空目标列短路，零塔对照的节拍只能测试侧计——F2①）
            if (boss.Alive && boss.TramplePeriodTicks > 0
                && tick - boss.SpawnTick > 0 && (tick - boss.SpawnTick) % boss.TramplePeriodTicks == 0)
            {
                pulses++;
            }

            if (!boss.Alive || boss.ArrivedAtTarget)
            {
                return new BattleOutcome(
                    Killed: !boss.Alive,
                    ArrivedAtBase: boss.ArrivedAtTarget,
                    EndTick: tick,
                    ShotsFired: towers.Sum(t => t.ShotsFired),
                    TowersDestroyed: destroyed,
                    TowerCount: towers.Count,
                    TotalDamage: towers.Sum(t => t.DamageDealt),
                    BossHpLost: boss.MaxHp - boss.Hp,
                    TramplePulses: pulses);
            }
        }

        return new BattleOutcome(
            false, false, WindowTicks, towers.Sum(t => t.ShotsFired), destroyed, towers.Count,
            towers.Sum(t => t.DamageDealt), boss.MaxHp - boss.Hp, pulses);
    }

    private static List<(int Col, int Row)> FlankingWall(int fromRow, int toRow) =>
        // 贴廊四列（横距 32/96 px，均在木箭塔 160 px 射程内；更远列永不入射界）
        (from col in new[] { PathCol - 2, PathCol - 1, PathCol + 1, PathCol + 2 }
         from row in Enumerable.Range(fromRow, toRow - fromRow + 1)
         select (col, row)).ToList();

    [Fact] // legal — 火力墙击杀线：Boss 在 60 s 内被击杀，且射弹数与 HP 守恒
    public void Stomper_dies_inside_the_flanking_tower_wall_within_sixty_seconds()
    {
        var outcome = RunBattle(FlankingWall(2, 31));

        // 手算（towers.json 木箭塔 d=10、reload=30、range=5 格；Boss 甲 6）：
        // 单发有效伤 = max(10−6, ceil(10%×10)) = 4；击穿 3750 HP 至少需
        // ceil(3750/4) = 938 发；理论最快全效力 = 3750/(120 塔×4/s) = 7.8 s；
        // 纯行进到达基地需 ~1387 ticks——击杀必须发生在到达之前。
        int effPerShot = Math.Max(10 - 6, (10 + 9) / 10);
        Assert.Equal(4, effPerShot);
        int minShots = (int)Math.Ceiling(3750.0 / effPerShot);
        long theoreticalFastestTicks = (long)Math.Ceiling(3750.0 / (outcome.TowerCount * effPerShot) * 30);
        long expectedArrival = (long)Math.Ceiling((33.0 * CellSize - 16.0) / 0.75);

        Assert.True(outcome.Killed, $"Boss 未被击杀（终局={outcome.ArrivedAtBase}, 末刻={outcome.EndTick}）");
        Assert.False(outcome.ArrivedAtBase);
        Assert.InRange(outcome.EndTick, theoreticalFastestTicks, WindowTicks); // ≥ 理论最快，≤ 60 s
        Assert.True(outcome.ShotsFired >= minShots, $"射弹 {outcome.ShotsFired} 发不足以造成 3750 伤害");
        Assert.True(outcome.ShotsFired <= outcome.TowerCount * (int)(WindowTicks / 30) + outcome.TowerCount,
            "射弹数超过供弹物理上限");
        Assert.True(outcome.EndTick < expectedArrival, "击杀必须发生在推进到基地之前（数量级与手算一致）");
        Assert.True(outcome.TowersDestroyed >= 1, "贴廊塔应被践踏摧毁至少一座（×1.3 伤践踏参战）");

        // 伤害守恒上界（batch2 审查 F2③）：总有效伤害 ≤ Boss 初始 HP + 单发溢杀上限。
        // 推导：d=10、甲=6 全程固定 ⇒ 每发结算值恒为 4；总伤害 = 4×已结算发数
        //（TestTower 只对存活 Boss 结算，死后同 tick 的后续塔空过）；Boss 起始 3750。
        // 937×4=3748 < 3750 ≤ 938×4=3752 ⇒ 击杀恰发生在第 938 发结算，总数=3752；
        // 溢杀仅可能发生在最后一发：溢出量 = 4 − 末发前余量 ≤ 3（单发上限 4 的口径），
        // 且总数 ∈ 4 的倍数 ⇒ 总 ∈ {3750..3750+4} ∩ 4ℤ = {3752}——区间断言写上界口径，
        // 等值断言钉精确值。
        Assert.InRange(outcome.TotalDamage, 3750, 3750 + effPerShot);
        Assert.Equal(3752, outcome.TotalDamage);
        Assert.Equal(3750, outcome.BossHpLost); // Boss 视角：满额 HP 清零（ApplyDamage 的 0 夹不掩盖守恒）
    }

    [Fact] // legal — 火力不足线：单塔拦不住，Boss 推进到基地，时刻与手算一致
    public void Stomper_reaches_the_base_against_a_single_tower_on_schedule()
    {
        var outcome = RunBattle(new List<(int Col, int Row)> { (PathCol - 1, 17) });

        // 手算：走廊 (6,0)→(6,33)，出生 Y=16，到达需 Y≥33×32=1056；
        // 1056−16 = 1040 px ÷ 0.75 px/tick ≈ 1387 ticks（±3 容差=到达 tick 取整）
        long expectedArrival = (long)Math.Ceiling((33.0 * CellSize - 16.0) / 0.75);

        Assert.False(outcome.Killed);
        Assert.True(outcome.ArrivedAtBase, "单塔火力远不足以拦 3750 HP，Boss 必达基地");
        Assert.InRange(outcome.EndTick, expectedArrival - 3, expectedArrival + 3);
        // 射弹上界推导（batch2 审查 F2②，替换原裸"~20 发"）：
        // 塔位 (5,17)→px(176,560)，Boss 沿 x=208 直行（0.75 px/tick 恒速，逐格目标
        // 切换不产生 Snap）⇒ 横距恒 32；入射界（严格 <）需 dy² < 160²−32²=24576
        // ⇒ |y−560| < 156.77；y(t)=16+0.75t ⇒ 入界 t>516.3（首刻 517），出界
        // t<934.4（末刻 934）——射击窗口 [517,934] 共 418 ticks；供弹周期 30、
        // NextFireTick 起点 0 ⇒ 至多 floor(418/30)+1 = 14 发（首发 517，末发
        // 517+30×13=907）。实测塔先被践踏摧毁（HP120，78 伤/45 tick，约 720 tick
        // 倒下 ⇒ ~7 发），14 为纯射界上界；单塔伤害上界 14×4=56 ≪ 3750。
        Assert.InRange(outcome.ShotsFired, 1, 14);
        Assert.True(outcome.TotalDamage <= 14 * 4, "单塔总伤害不得超出发数×单发上界");
    }

    [Fact] // boundary — 零塔对照：Boss 无伤推进，到达时刻即纯移动手算值
    public void Stomper_walks_unharmed_to_the_base_with_zero_towers()
    {
        var outcome = RunBattle(new List<(int, int)>(0));

        long expectedArrival = (long)Math.Ceiling((33.0 * CellSize - 16.0) / 0.75);
        // 有意义断言（batch2 审查 F2①，替换恒真式 Assert.Equal(0, ShotsFired)/
        // Equal(0, TowersDestroyed)——空列表 Sum 恒 0，红不了）：
        Assert.False(outcome.Killed, "零火力下 Boss 必存活至到达");
        Assert.True(outcome.ArrivedAtBase, "基地必须被突破（零塔时唯一结局）");
        Assert.InRange(outcome.EndTick, expectedArrival - 3, expectedArrival + 3);
        Assert.Equal(0, outcome.BossHpLost); // 累计伤害=0：以 Boss 实收结算计（MaxHp−Hp），可红
        Assert.True(outcome.TramplePulses > 0,
            $"Boss 全程应保持践踏节拍（~1387 ticks ÷ 周期 45 ≈ 30 次脉冲格点，实测 {outcome.TramplePulses}）");
    }
}
