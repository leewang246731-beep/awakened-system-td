using EraCore.Contracts.Foundation;
using EraCore.Energy;
using EraCore.Sim;
using EraCore.Tests.Energy;
using EraCore.Units;
using Xunit;
using static EraCore.Sim.SimBuildingKind;

namespace EraCore.Tests.Sim;

// [C-12][C-08][F-010] T-123 世界层 AOE/辐射区消费（W6-exit P1「AOE 消费面」清账）：
// EnergyRiskWorldConsumer 纯结算手算对照（建筑=最大 HP×fraction[半径内]、敌=flat 伤害
// 走 ApplyDamage 单管线、辐射区=−25% 速度+5/s HP 衰减批量）+ Simulation 第 (2) 段接线
// 集成（E3 过热爆炸/E4 熔毁共用路径）+ 确定性 + 非法参数。表值来自 Sp4RiskTables 夹具
// （era3/era4.json 数据侧锚定，此处不读内容文件；radiation decay=5=[NEW-T-123]=300÷60）。
public class EnergyRiskWorldConsumerTests
{
    // ---- 夹具 ---------------------------------------------------------------

    private static readonly CellCoord Origin = new(0, 0);

    /// <summary>SP-4 L146 E3 过热后果位（半径 4/建筑 50%/敌 300/无辐射区）。</summary>
    private static EnergyRiskOutcome OverheatOutcome(long tick = 1000) => new(
        Tick: tick,
        Kind: EnergyRiskEventKind.OverheatExplosion,
        Culprit: new EnergyNodeId(7001),
        LoadRatioAtTrigger: 1.2,
        RadiusCells: 4.0,
        BuildingDamageMaxHpFraction: 0.50,
        EnemyDamage: 300.0,
        GeneratorDowntimeSeconds: 60.0,
        GeneratorDerateFraction: 0.50,
        GeneratorDerateSeconds: 30.0,
        RadiationZoneSeconds: 0.0,
        RadiationEnemySlowFraction: 0.0,
        WreckRecoveryCostFraction: 0.0);

    /// <summary>SP-4 L147 E4 熔毁后果位（半径 6/建筑 80%/敌 0/污染 60 s −25%）。</summary>
    private static EnergyRiskOutcome MeltdownOutcome(long tick = 1000) => new(
        Tick: tick,
        Kind: EnergyRiskEventKind.Meltdown,
        Culprit: new EnergyNodeId(7002),
        LoadRatioAtTrigger: 1.2,
        RadiusCells: 6.0,
        BuildingDamageMaxHpFraction: 0.80,
        EnemyDamage: 0.0,
        GeneratorDowntimeSeconds: 0.0,
        GeneratorDerateFraction: 0.0,
        GeneratorDerateSeconds: 0.0,
        RadiationZoneSeconds: 60.0,
        RadiationEnemySlowFraction: 0.25,
        WreckRecoveryCostFraction: 0.30);

    private static SimBuilding BuildingAt(int id, int x, int y, int hp, int? maxHp = null) => new(
        new EntityId(id), "t123test:building", new CellCoord(x, y), hp, 1, Tower, maxHp);

    /// <summary>锚 (0,0) 格中心 (16,16)；偏移 n 格的单位（1 格=32 px，K-1 域）。</summary>
    private static Unit UnitAt(int id, double cellsEast, int maxHp, int armor, float speed = 1.6f) =>
        new(new EntityId(id), "t123test:enemy", 16f + (float)(cellsEast * 32), 16f, maxHp, armor, speed);

    // ---- 1) E3 过热爆炸 AOE 手算（纯结算） -----------------------------------

    [Fact] // legal —— 建筑半径内吃最大 HP×50%，半径边缘含等号、界外零伤
    public void Overheat_blast_damages_buildings_at_half_max_hp_inside_radius_four()
    {
        var consumer = new EnergyRiskWorldConsumer();
        var rows = new List<SimBuilding>
        {
            BuildingAt(1, 0, 0, 120),   // 距离 0：round(120×0.5)=60 → 60
            BuildingAt(2, 4, 0, 120),  // 距离 4（边界含等号）：60 → 60
            BuildingAt(3, 0, 3, 120),  // 距离 3：60 → 60
            BuildingAt(4, 3, 4, 120),  // 距离 5>4：不写
            BuildingAt(5, 0, 0, 30, maxHp: 120), // 已损行：仍按最大 HP 咬 60 → 落 0
        };
        var replaced = new List<long>();

        var report = consumer.ApplyBlast(
            OverheatOutcome(), 1000, Origin, rows,
            (index, damaged) =>
            {
                replaced.Add(rows[index].Id.Value);
                rows[index] = damaged;
            },
            Array.Empty<Unit>());

        // 手算（SP-4 L146 后果列）：4 行落弹（界外 1 行不动）；已损行按 MAX 咬半
        Assert.Equal(4, report.BuildingsHit);
        Assert.Equal(new long[] { 1, 2, 3, 5 }, replaced);
        Assert.Equal(60, rows[0].Hp);
        Assert.Equal(60, rows[1].Hp);
        Assert.Equal(60, rows[2].Hp);
        Assert.Equal(120, rows[3].Hp);
        Assert.Equal(0, rows[4].Hp);
        Assert.Equal(120, rows[4].MaxHpResolved); // 分母=最大 HP（非当前行）
    }

    [Fact] // legal —— 敌 300 flat 走 ApplyDamage 单管线（盾→甲→HP）手算
    public void Overheat_blast_settles_300_on_enemies_through_the_damage_pipeline()
    {
        var consumer = new EnergyRiskWorldConsumer();
        var noArmor = UnitAt(21, 2, maxHp: 240, armor: 0);      // DirectInt(300,0)=300 → 240 HP 阵亡（溢出计 300）
        var heavyArmor = UnitAt(22, 4, maxHp: 3000, armor: 16); // DirectInt(300,16)=284 → 2716
        var shielded = UnitAt(23, 1, maxHp: 100, armor: 8);     // 盾 240 吸 240 → 余 60：DirectInt(60,8)=52 → 48
        shielded.SetShield(240);
        var outside = UnitAt(24, 4.5, maxHp: 3000, armor: 0);   // 距离 4.5>4：零伤
        var units = new[] { noArmor, heavyArmor, shielded, outside };

        var report = consumer.ApplyBlast(
            OverheatOutcome(), 1000, Origin,
            new List<SimBuilding>(), (_, _) => { }, units);

        Assert.Equal(3, report.EnemiesHit);
        // 手算：300（溢出阵亡）+ 284 + 240 盾 + 52 血 = 876
        Assert.Equal(876, report.EnemyDamageSettled);
        Assert.False(noArmor.Alive);
        Assert.Equal(2716, heavyArmor.Hp);
        Assert.Equal(48, shielded.Hp);
        Assert.True(outside.Alive);
        Assert.Equal(3000, outside.Hp);
    }

    [Fact] // legal —— E4 熔毁：敌伤=0（SP-4 L147 只伤建筑），半径内单位零落弹
    public void Meltdown_blast_hits_buildings_only_at_eighty_percent()
    {
        var consumer = new EnergyRiskWorldConsumer();
        var rows = new List<SimBuilding> { BuildingAt(1, 0, 0, 120) };
        var inside = UnitAt(21, 1, maxHp: 3000, armor: 0);

        var report = consumer.ApplyBlast(
            MeltdownOutcome(), 1000, Origin, rows,
            (index, damaged) => rows[index] = damaged,
            new[] { inside });

        Assert.Equal(1, report.BuildingsHit);
        Assert.Equal(0, report.EnemiesHit);        // EnemyDamage=0 → 敌零落弹
        Assert.Equal(0, report.EnemyDamageSettled);
        Assert.Equal(24, rows[0].Hp);              // round(120×0.8)=96 → 120−96=24
        Assert.Equal(3000, inside.Hp);             // 单位无伤
    }

    // ---- 2) E4 辐射区：spawn/tick/prune 手算 ---------------------------------

    [Fact] // legal —— 熔毁辐射区入册：锚/半径/时长/减速全 SP-4 L147 值
    public void Meltdown_spawns_the_radiation_zone_with_table_parameters()
    {
        var consumer = new EnergyRiskWorldConsumer(radiationHpDecayPerSecond: 5.0);

        RadiationZone? zone = consumer.SpawnZone(MeltdownOutcome(), Origin, tick: 2070);

        Assert.NotNull(zone);
        Assert.Equal(Origin, zone!.Anchor);
        Assert.Equal(6.0, zone.RadiusCells, 9);        // 半径=熔毁半径 6
        Assert.Equal(2070, zone.StartTick);
        Assert.Equal(2070 + 1800, zone.UntilTick);     // 60 s × 30 Hz = 1800 ticks
        Assert.Equal(25, zone.SlowPercent);            // 过境敌 −25% 速度
        Assert.Equal(5.0, zone.HpDecayPerSecond, 9);   // [NEW-T-123]=300÷60
        Assert.Single(consumer.RadiationZones);

        // E3 过热后果位不带区（RadiationZoneSeconds=0）→ SpawnZone 返回 null
        Assert.Null(consumer.SpawnZone(OverheatOutcome(), Origin, 2071));
        Assert.Single(consumer.RadiationZones);
    }

    [Fact] // legal —— 区内过境敌 −25%（Re-arm 每拍），离区即脱
    public void Zone_slow_hits_crossing_enemies_and_sheds_after_leaving()
    {
        var consumer = new EnergyRiskWorldConsumer();
        _ = consumer.SpawnZone(MeltdownOutcome(), Origin, tick: 1000);
        var inside = UnitAt(21, 3, maxHp: 600, armor: 0, speed: 1.6f);
        var outside = UnitAt(22, 10, maxHp: 600, armor: 0, speed: 1.6f);
        var units = new[] { inside, outside };

        consumer.TickZones(1000, units);
        inside.ResolveSpeedEffects(1000);
        outside.ResolveSpeedEffects(1000);

        // 手算：1.6 px/tick × (1 − 25%) = 1.2；界外不受影响
        Assert.Equal(1.2, inside.EffectiveSpeedPxPerTick, 5);
        Assert.Equal(1.6, outside.EffectiveSpeedPxPerTick, 5);
        Assert.Equal(-25, inside.ResolvedSpeedBonusPercent);

        // 界内单位挪出（10 格）后：短窗（tick+2）过期即恢复全速
        var moved = UnitAt(21, 3, maxHp: 600, armor: 0, speed: 1.6f);
        consumer.TickZones(1000, new[] { moved });
        moved.ResolveSpeedEffects(1000);
        Assert.Equal(1.2, moved.EffectiveSpeedPxPerTick, 5);
        moved.ResolveSpeedEffects(1003); // 1002 过期，1003 拍恢复
        Assert.Equal(1.6, moved.EffectiveSpeedPxPerTick, 5);
    }

    [Fact] // legal + boundary —— HP 衰减批量：每满 30 tick（区龄秒界）结一次 5 点
    public void Zone_hp_decay_batches_once_per_second_on_the_zone_age_boundary()
    {
        var consumer = new EnergyRiskWorldConsumer(radiationHpDecayPerSecond: 5.0);
        _ = consumer.SpawnZone(MeltdownOutcome(), Origin, tick: 1000);
        var armored = UnitAt(21, 3, maxHp: 600, armor: 2); // DirectInt(5,2)=max(3, ceil(0.5))=3/批
        var bare = UnitAt(22, 4, maxHp: 600, armor: 0);    // 5/批

        for (long tick = 1001; tick <= 1060; tick++)
        {
            consumer.TickZones(tick, new[] { armored, bare });
        }

        // 手算：1029（区龄 29）无批量；1030（龄 30）与 1060（龄 60）各一批
        // → 甲 2：600−3−3=594；甲 0：600−5−5=590
        Assert.Equal(594, armored.Hp);
        Assert.Equal(590, bare.Hp);
    }

    [Fact] // legal —— 全程滞留 60 s 的对敌总伤=300（[NEW-T-123]=300÷60 推导的预算锚）
    public void Full_sixty_second_stand_inside_the_zone_costs_three_hundred_points()
    {
        var consumer = new EnergyRiskWorldConsumer(radiationHpDecayPerSecond: 5.0);
        _ = consumer.SpawnZone(MeltdownOutcome(), Origin, tick: 1000);
        var stayer = UnitAt(21, 3, maxHp: 600, armor: 0);

        for (long tick = 1001; tick <= 2800; tick++)
        {
            consumer.TickZones(tick, new[] { stayer });
        }

        // 手算：60 批 × 5 = 300（区龄 30..1800 恰 60 个秒界；末拍 2800=UntilTick 仍活）
        Assert.Equal(300, stayer.Hp);
        Assert.Single(consumer.RadiationZones);
    }

    [Fact] // boundary —— 区过期即剪除：UntilTick 当拍仍活、次拍出列
    public void Zone_expires_and_is_pruned_after_the_duration()
    {
        var consumer = new EnergyRiskWorldConsumer();
        _ = consumer.SpawnZone(MeltdownOutcome(), Origin, tick: 1000);

        consumer.TickZones(2800, Array.Empty<Unit>()); // 60 s 末拍：仍在册
        Assert.Single(consumer.RadiationZones);

        consumer.TickZones(2801, Array.Empty<Unit>()); // 过期剪除
        Assert.Empty(consumer.RadiationZones);
    }

    [Fact] // legal —— 确定性：同 outcomes+同世界 ⇒ 同报告/同哈希
    public void Consumer_is_deterministic_across_equal_runs()
    {
        (RiskBlastReport Report, long Hash, int Hp) RunOnce()
        {
            var consumer = new EnergyRiskWorldConsumer(radiationHpDecayPerSecond: 5.0);
            var rows = new List<SimBuilding> { BuildingAt(1, 0, 0, 120) };
            var report = consumer.ApplyBlast(
                OverheatOutcome(), 1000, Origin, rows,
                (index, damaged) => rows[index] = damaged,
                new[] { UnitAt(21, 2, 240, 0) });
            _ = consumer.SpawnZone(MeltdownOutcome(), Origin, 1000);
            for (long tick = 1001; tick <= 1100; tick++)
            {
                consumer.TickZones(tick, Array.Empty<Unit>());
            }

            return (report, consumer.ComputeStateHash(), rows[0].Hp);
        }

        var a = RunOnce();
        var b = RunOnce();
        Assert.Equal(a.Report, b.Report);
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(a.Hp, b.Hp);
    }

    // ---- 3) 非法参数与接线缺陷 ----------------------------------------------

    [Theory] // illegal —— 构造参数非法必须拒绝（CS-3.5 负向）
    [InlineData(0.0, 5.0)]     // tick 域非正
    [InlineData(-30.0, 5.0)]   // tick 域负
    [InlineData(30.0, -1.0)]   // 衰减率负
    [InlineData(30.0, double.NaN)]
    public void Constructor_rejects_hostile_arguments(double ticksPerSecond, double decay)
    {
        Assert.Throws<ArgumentException>(
            () => new EnergyRiskWorldConsumer(ticksPerSecond, decay));
    }

    [Fact] // illegal —— SimulationConfig 侧同一守卫（辐射衰减率负 → 构造即红）
    public void Simulation_config_rejects_negative_radiation_decay()
    {
        Assert.Throws<ArgumentException>(() => _ = new Simulation(
            SimE1Fixture.Config() with { RadiationZoneHpDecayPerSecond = -0.1 },
            new SimRng(SimE1Fixture.Seed),
            SimE1Fixture.Seed));
    }

    // ---- 4) Simulation 第 (2) 段接线：E3 过热（真实关卡夹具） -----------------

    [Fact] // legal —— E3 过热：tick 1619 爆炸落世界层——半径 4 内建筑恰吃最大 HP×50%
    public void Overheat_blast_lands_on_sim_buildings_from_the_energy_segment()
    {
        // 布局（E1 夹具坐标）：营 (4,20)、采集器 (6,18)、塔 (6,21)、仓库 (2,14)。
        // 距锚 (4,20)：塔 √5≈2.24≤4 落弹；采集器 √8≈2.83≤4 落弹；仓库 √40≈6.32>4 不落。
        // 手算：木箭塔 HP 120（era1 towers.json）→ round(120×0.5)=60 → 60；
        // 采集器/营=EconomyBuildingHp 1 → round(1×0.5)=1 → 0。
        var sim = new Simulation(
            SimE1Fixture.Config() with { EnergyRisk = Sp4RiskTables.OverheatOnly() },
            new SimRng(SimE1Fixture.Seed),
            SimE1Fixture.Seed);
        sim.EnqueueRange(new[]
        {
            SimCommands.BuildLaborCamp(60, SimE1Fixture.LaborCampCell),
            SimCommands.BuildHarvester(120, SimE1Fixture.HarvesterCell, SimE1Fixture.Wood, seats: 10),
            SimCommands.BuildTower(240, "era1:tower-wood-arrow", SimE1Fixture.TowerCell),
        });

        sim.Run(1618);
        Assert.Empty(sim.Ledger.RiskEvents); // N−1：前一 tick 未触发，世界层零落弹
        Assert.Equal(120, sim.Buildings.Single(b => b.ContentId == "era1:tower-wood-arrow").Hp);

        sim.Run(2); // tick 1619 爆炸、1620 复核
        EnergyRiskOutcome outcome = Assert.Single(sim.Ledger.RiskEvents);
        Assert.Equal(EnergyRiskEventKind.OverheatExplosion, outcome.Kind);

        Assert.Equal(60, sim.Buildings.Single(b => b.ContentId == "era1:tower-wood-arrow").Hp);
        Assert.Equal(0, sim.Buildings.Single(b => b.Kind == Harvester).Hp);
        Assert.Equal(0, sim.Buildings.Single(b => b.Kind == LaborCamp).Hp);
        Assert.Equal(1, sim.Buildings.Single(b => b.Kind == Warehouse).Hp); // 界外零伤
        Assert.Empty(sim.RiskWorld.RadiationZones); // E3 后果位不带辐射区

        // 停机窗口写回账本（T-113 已锚）：有效发电 0
        Assert.Equal(0.0, sim.Ledger.TotalOutputPerSecond, 9);
    }

    [Fact] // legal —— 同 seed 同命令流 ⇒ 每 tick 哈希一致（世界层消费不破坏重放）
    public void Overheat_world_consumption_stays_replay_deterministic()
    {
        long HashOfFreshRun()
        {
            var sim = new Simulation(
                SimE1Fixture.Config() with { EnergyRisk = Sp4RiskTables.OverheatOnly() },
                new SimRng(SimE1Fixture.Seed),
                SimE1Fixture.Seed);
            sim.EnqueueRange(new[]
            {
                SimCommands.BuildLaborCamp(60, SimE1Fixture.LaborCampCell),
                SimCommands.BuildHarvester(120, SimE1Fixture.HarvesterCell, SimE1Fixture.Wood, seats: 10),
                SimCommands.BuildTower(240, "era1:tower-wood-arrow", SimE1Fixture.TowerCell),
            });
            sim.Run(1700);
            return sim.ComputeStateHash();
        }

        Assert.Equal(HashOfFreshRun(), HashOfFreshRun());
    }

    // ---- 5) Simulation 第 (2) 段接线：E4 熔毁（辐射区+永久损毁） --------------

    /// <summary>
    /// 仅熔毁通道的表（过热行阈值抬到 2.0=本夹具负载域不可达——隔离两通道，
    /// 生产侧 E4 两行同挂；数值仍取 Sp4RiskTables 语义值）。
    /// </summary>
    private static EnergyRiskTuning MeltdownOnly() => new(
        Sp4RiskTables.Overheat with { OverloadLoadRatio = 2.0 },
        Sp4RiskTables.Meltdown);

    [Fact] // legal —— E4 熔毁：tick 2069 落世界层——×80% 建筑+辐射区入册+肇事机注销
    public void Meltdown_lands_on_sim_buildings_spawns_the_zone_and_destroys_the_producer()
    {
        // 负载手算：营 10 点/s@60 + 采集器 12 坐席（12 点/s）@120 → 自 settle 120 起
        // 负载 12/10=120%；持续 20 s=600 settle → 719 武装倒计时（45 s=1350 tick）；
        // 自 720 逐拍递减 → 2069 归零熔毁。
        // 落点：塔 (6,21) 距锚 (4,20) √5≈2.24≤6 → round(120×0.8)=96 → 24；
        // 仓库 √40≈6.32>6 界外；辐射区=锚 (4,20) 半径 6 至 2069+1800。
        var sim = new Simulation(
            SimE1Fixture.Config() with
            {
                EnergyRisk = MeltdownOnly(),
                RadiationZoneHpDecayPerSecond = 5.0,
            },
            new SimRng(SimE1Fixture.Seed),
            SimE1Fixture.Seed);
        sim.EnqueueRange(new[]
        {
            SimCommands.BuildLaborCamp(60, SimE1Fixture.LaborCampCell),
            SimCommands.BuildHarvester(120, SimE1Fixture.HarvesterCell, SimE1Fixture.Wood, seats: 12),
            SimCommands.BuildTower(240, "era1:tower-wood-arrow", SimE1Fixture.TowerCell),
        });

        sim.Run(2068);
        Assert.Empty(sim.Ledger.RiskEvents); // N−1：倒计时未归零
        Assert.Empty(sim.RiskWorld.RadiationZones);

        sim.Step(); // tick 2069 熔毁
        EnergyRiskOutcome outcome = Assert.Single(sim.Ledger.RiskEvents);
        Assert.Equal(EnergyRiskEventKind.Meltdown, outcome.Kind);
        Assert.Equal(2069, outcome.Tick);

        // 账本域后果（T-113 已锚）：肇事机永久注销（残骸）
        Assert.Equal(0, sim.Ledger.ProducerCount);

        // 世界层后果：建筑 ×80% + 辐射区入册（全 SP-4 L147 表值）
        Assert.Equal(24, sim.Buildings.Single(b => b.ContentId == "era1:tower-wood-arrow").Hp);
        Assert.Equal(1, sim.Buildings.Single(b => b.Kind == Warehouse).Hp); // 界外零伤
        RadiationZone zone = Assert.Single(sim.RiskWorld.RadiationZones);
        Assert.Equal(SimE1Fixture.LaborCampCell, zone.Anchor);
        Assert.Equal(6.0, zone.RadiusCells, 9);
        Assert.Equal(2069, zone.StartTick);
        Assert.Equal(2069 + 1800, zone.UntilTick);
        Assert.Equal(25, zone.SlowPercent);
        Assert.Equal(5.0, zone.HpDecayPerSecond, 9);
    }

    [Fact] // legal —— E4 熔毁后同 seed 重放一致（含辐射区哈希折叠）
    public void Meltdown_world_consumption_stays_replay_deterministic()
    {
        long HashOfFreshRun()
        {
            var sim = new Simulation(
                SimE1Fixture.Config() with
                {
                    EnergyRisk = MeltdownOnly(),
                    RadiationZoneHpDecayPerSecond = 5.0,
                },
                new SimRng(SimE1Fixture.Seed),
                SimE1Fixture.Seed);
            sim.EnqueueRange(new[]
            {
                SimCommands.BuildLaborCamp(60, SimE1Fixture.LaborCampCell),
                SimCommands.BuildHarvester(120, SimE1Fixture.HarvesterCell, SimE1Fixture.Wood, seats: 12),
                SimCommands.BuildTower(240, "era1:tower-wood-arrow", SimE1Fixture.TowerCell),
            });
            sim.Run(2100);
            return sim.ComputeStateHash();
        }

        Assert.Equal(HashOfFreshRun(), HashOfFreshRun());
    }

    [Fact] // legal —— 无风险表关卡（E1/E2 语义）：世界层消费零介入、无辐射区
    public void Riskless_level_never_touches_the_world_consumer()
    {
        var sim = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        sim.EnqueueRange(SimE1Fixture.MilestoneCommands());

        sim.Run(3000);

        Assert.Empty(sim.Ledger.RiskEvents);
        Assert.Empty(sim.RiskWorld.RadiationZones);
        Assert.All(sim.Buildings, b => Assert.Equal(b.MaxHpResolved, b.Hp)); // 全程零损伤
    }
}
