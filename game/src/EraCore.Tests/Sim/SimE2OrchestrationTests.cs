using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Logistics;
using EraCore.Sim;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Sim;

// [C-12][F-001][F-014][F-155] E2 全子系统编排集成（T-067 验收：各系统活性断言）：
// E2 塔链（升级/出售残骸）、护盾破+再生、英雄技能 CD、六档物流逐档交付、
// 传送阵能量钩扣能、弹影层在飞——真实命令流驱动，关键值锚点手算抽查。
public class SimE2OrchestrationTests
{
    private const long RunTicks = 3_600; // 120 s @ 30 Hz：覆盖首波（t=450）全程+第二波（t=2250）入场

    private static Simulation RunMilestone()
    {
        var sim = new Simulation(SimE2Fixture.Config(), new SimRng(SimE2Fixture.Seed), SimE2Fixture.Seed);
        sim.EnqueueRange(SimE2Fixture.MilestoneCommands());
        sim.Run(RunTicks);
        return sim;
    }

    [Fact]
    public void Every_milestone_command_applies()
    {
        Simulation sim = RunMilestone();

        // 25 条命令全部落地（拒绝=夹具坐标/语义缺陷，直接红并点名）
        var rejected = sim.CommandResults.Where(r => !r.Accepted).ToList();
        Assert.True(rejected.Count == 0,
            $"夹具命令被拒绝：{string.Join("; ", rejected.Select(r => $"{r.Kind}@{r.Tick}={r.RejectionReason}"))}");
        Assert.Equal(SimE2Fixture.MilestoneCommands().Count, sim.AppliedCommandCount);
    }

    [Fact]
    public void Six_tier_logistics_all_deliver()
    {
        Simulation sim = RunMilestone();

        // 民夫（档1）：harvester1→converter1 木材线
        Carrier porter = sim.Carriers[0];
        Assert.Equal(TransportTiers.Porter.Id, porter.Tier.Id);
        Assert.True(porter.DeliveredItems >= 1, $"民夫线应有交付（实际 {porter.DeliveredItems}）");

        // 马车（档2）：converter1→tower1 箭矢线
        Carrier cart = sim.Carriers[1];
        Assert.Equal(TransportTiers.Cart.Id, cart.Tier.Id);
        Assert.True(cart.DeliveredItems >= 1, $"马车线应有交付（实际 {cart.DeliveredItems}）");

        // 矿轨（档3）：harvester2→仓库 木材线
        Carrier rail = sim.Carriers[2];
        Assert.Equal(TransportTiers.MineRail.Id, rail.Tier.Id);
        Assert.True(rail.DeliveredItems >= 1, $"矿轨线应有交付（实际 {rail.DeliveredItems}）");

        // 传送带（档4）：harvester1→converter1 连续流
        ConveyorBelt belt = sim.Belts[0];
        Assert.True(belt.DeliveredItems >= 1, $"传送带应有交付（实际 {belt.DeliveredItems}）");

        // 无人机（档5）：harvester2→converter2 直线飞行（下标 4——660 的第二条民夫线在前）
        Carrier drone = sim.Carriers[4];
        Assert.Equal(TransportTiers.Drone.Id, drone.Tier.Id);
        Assert.True(drone.DeliveredItems >= 1, $"无人机线应有交付（实际 {drone.DeliveredItems}）");

        // 传送阵（档6）：converter2→火油塔弹匣，瞬时+能量计价
        SimTeleporterLane lane = sim.Teleporters[0];
        Assert.True(lane.Link.DeliveredItems >= 1, $"传送阵应有交付（实际 {lane.Link.DeliveredItems}）");
        Assert.Equal(lane.Link.DeliveredItems * TeleporterLink.EnergyCostPerItem, lane.Link.EnergySpent);
        Assert.True(lane.Link.EnergySpent > 0, "传送阵应已扣能（能量=唯一限流器）");

        // 传送阵的消费节点在账本上可查（SatisfactionOf 不抛 unknown id 即在册）
        _ = sim.Ledger.SatisfactionOf(lane.NodeId);
    }

    [Fact]
    public void Teleporter_energy_budget_follows_ledger_satisfaction()
    {
        // [F-042] 能量钩子时点抽查：传送阵 t=860 落地，账本此时 7 营地满供给
        var sim = new Simulation(SimE2Fixture.Config(), new SimRng(SimE2Fixture.Seed), SimE2Fixture.Seed);
        sim.EnqueueRange(SimE2Fixture.MilestoneCommands());
        sim.Run(870);

        SimTeleporterLane lane = sim.Teleporters[0];
        double satisfaction = sim.Ledger.SatisfactionOf(lane.NodeId);
        Assert.True(satisfaction > 0.99, $"7 营地（70/s）对 60/s 传送需求+经济消耗应近满供（实际 {satisfaction:0.###}）");

        // 手算锚点：满供下每 tick 产 floor(1.0×60/30)=2 单位 → 落地 10 tick 后
        // 银行积累+已消耗合计 ≥20 单位（整数银行口径）
        Assert.True(lane.Port.Available + lane.Link.EnergySpent >= 20,
            $"能量银行口径失守（剩余 {lane.Port.Available}+已耗 {lane.Link.EnergySpent}）");
    }

    [Fact]
    public void Shield_breaks_and_regenerates_under_fire()
    {
        var sim = new Simulation(SimE2Fixture.Config(), new SimRng(SimE2Fixture.Seed), SimE2Fixture.Seed);
        sim.EnqueueRange(SimE2Fixture.MilestoneCommands());

        bool sawShielded = false;
        bool sawBroken = false;
        bool sawRegen = false;
        var shieldSeen = new HashSet<long>();
        var brokenSeen = new HashSet<long>();
        for (long t = 1; t <= RunTicks; t++)
        {
            sim.Step();
            foreach (Unit unit in sim.Units)
            {
                if (!unit.Alive)
                {
                    continue;
                }

                if (unit.Shield > 0)
                {
                    sawShielded = true;
                    if (brokenSeen.Contains(unit.Id.Value))
                    {
                        // 破盾后盾值回正：再生通道活跃（v3.2 §6 破盾 150t 后 20/s）
                        sawRegen = true;
                    }

                    shieldSeen.Add(unit.Id.Value);
                }
                else if (shieldSeen.Contains(unit.Id.Value))
                {
                    // 曾有盾 → 现在 0：破盾（火力打空）
                    brokenSeen.Add(unit.Id.Value);
                    sawBroken = true;
                }
            }
        }

        Assert.True(sawShielded, "ShieldStartWave=1 波次盾应生效（框架活性）");
        Assert.True(sawBroken, "火力下应有护盾被打空（破盾）");
        Assert.True(sawRegen, "破盾 150t 后应观察到 20/s 再生（v3.2 §6）");
    }

    [Fact]
    public void Hero_move_order_relocates_and_skill_order_is_deterministic()
    {
        Simulation sim = RunMilestone();

        Hero? hero = sim.Hero;
        Assert.NotNull(hero);
        Assert.Equal("era2:hero-spearwarden", hero!.ContentId);

        // move 令（t=1100 → (9,20)）：英雄离开家（(10,24) 中心 336,800 → 目标 304,656）
        Assert.True(
            Math.Abs(hero.X - ((SimE2Fixture.HeroCell.X * 32f) + 16f)) > 1f
            || Math.Abs(hero.Y - ((SimE2Fixture.HeroCell.Y * 32f) + 16f)) > 1f,
            "英雄应已离开出生点（move 令生效）");

        // skill 令（t=1300）：Fired ⇒ 40s=1200t 冷却；NoTargets ⇒ 冷却不动——两个
        // 结局都断言其确定性可观察面。
        Assert.True(
            hero.NextSkillReadyTick == long.MinValue || hero.NextSkillReadyTick > 1300,
            $"技能冷却时点应一致（实际 {hero.NextSkillReadyTick}）");
        Assert.True(hero.Alive, "E2 敌无攻击英雄路径，英雄应存活");
    }

    [Fact]
    public void Hero_skill_fires_and_cools_down_when_enemies_reach_the_base()
    {
        // 定向复跑（无英雄令的命令流）：英雄留驻基地旁（家=(10,24)，距基地 1 格），
        // 轮询到活敌进入 2 格技能距离后下令——断言 Fired 的可观察面：
        // NextSkillReadyTick 前进恰 1200t（40s CD @30Hz，SP-6），CD 内再令不重置。
        var sim = new Simulation(SimE2Fixture.Config(), new SimRng(SimE2Fixture.Seed), SimE2Fixture.Seed);
        sim.EnqueueRange(SimE2Fixture.MilestoneCommands()
            .Where(c => !c.PayloadJson.Contains("\"hero-order\"")));

        Hero hero = sim.Hero!;
        long fireTick = -1;
        for (long t = 1; t <= RunTicks && fireTick < 0; t++)
        {
            sim.Step();
            foreach (Unit unit in sim.Units)
            {
                if (!unit.Alive)
                {
                    continue;
                }

                float dx = unit.X - hero.X;
                float dy = unit.Y - hero.Y;
                if ((dx * dx) + (dy * dy) <= (2 * 32f) * (2 * 32f))
                {
                    fireTick = t;
                    break;
                }
            }
        }

        Assert.True(fireTick > 0, "敌群应抵达基地旁（英雄家距基地 1 格，技能距 2 格）");
        sim.Enqueue(SimE2Commands.HeroOrderSkill(fireTick + 1));
        sim.Run(1);
        Assert.Equal(fireTick + 1 + 1_200, hero.NextSkillReadyTick); // 40 s × 30 Hz（SP-6 冷却锚点）

        // CD 内再令：命令 applied 但冷却不动（OnCooldown 运行结局——不重置）
        long ready = hero.NextSkillReadyTick;
        sim.Enqueue(SimE2Commands.HeroOrderSkill(fireTick + 2));
        sim.Run(1);
        Assert.Equal(ready, hero.NextSkillReadyTick);
    }

    [Fact]
    public void Upgrade_rebuilds_tower_and_sell_leaves_ruin()
    {
        Simulation sim = RunMilestone();

        // 升级（t=940）：tower1 箭楼→弩塔，同 id 同格，武器/塔层换新
        SimTower upgraded = Assert.Single(sim.Towers, t => t.Id.Value == SimE2Fixture.IdTower1);
        Assert.Equal("era2:tower-crossbow", upgraded.Spec.Id);
        Assert.Equal(2, upgraded.Spec.Tier);
        Assert.True(upgraded.Weapon.FiredCount > 0, "升级后的弩塔应已开火");

        // 升级后弹药线换弹系（era2:ammo-stone-bolt），马车交付继续入新弹匣
        Assert.True(sim.TryGetTowerMagazine(new EntityId(SimE2Fixture.IdTower1), out var magazine));
        Assert.Equal("era2:ammo-stone-bolt", magazine.Ammo.Value);

        // 出售（t=1600）：tower2 退役，残骸入账占格，30% 退款
        Assert.True(!sim.Towers.Any(t => t.Id.Value == SimE2Fixture.IdTower2), "出售后塔应从塔组退役");
        Assert.True(!sim.Buildings.Any(b => b.Id.Value == SimE2Fixture.IdTower2), "出售后建筑薄记应移除");
        TowerRuin ruin = Assert.Single(sim.Estate.Ruins);
        Assert.Equal(SimE2Fixture.Tower2Cell, ruin.Cell);
        Assert.Equal("era2:tower-arrow", ruin.ContentId);
        Assert.Equal(1, ruin.Tier);
        Assert.Equal(27.0, ruin.SellRefundPoints, 6); // round(90×0.30)=27（v3.2 §11.4）
        Assert.Equal(27.0, ruin.RepairCostPoints, 6); // round(90×0.30)=27（FailureTiers 30% 口径）
        Assert.True(sim.World.IsOccupied(ruin.Cell), "残骸应占格（重建被阻直至修复）");

        // 残骸格上再建塔应被拒（cell-occupied）
        sim.Enqueue(SimE2Commands.BuildTower(3_000, "era2:tower-arrow", SimE2Fixture.Tower2Cell));
        sim.Run(1);
        Assert.NotNull(sim.CommandResults[^1].RejectionReason);
        Assert.StartsWith("cell-occupied", sim.CommandResults[^1].RejectionReason);
    }

    [Fact]
    public void Sell_without_confirm_is_refused()
    {
        // [F-014] 危险位默认拒绝：无 confirm_sell 的出售命令必须被拒且不产生残骸
        var sim = new Simulation(SimE2Fixture.Config(), new SimRng(SimE2Fixture.Seed), SimE2Fixture.Seed);
        sim.EnqueueRange(SimE2Fixture.MilestoneCommands());
        sim.Run(700); // tower2 t=620 已落地
        sim.Enqueue(SimE2Commands.SellTower(710, SimE2Fixture.IdTower2, confirm: false));
        sim.Run(11); // 命令 t=710 落地（run 到 711）

        Assert.NotNull(sim.CommandResults[^1].RejectionReason);
        Assert.StartsWith("confirm-required", sim.CommandResults[^1].RejectionReason);
        Assert.Empty(sim.Estate.Ruins);
        Assert.Contains(sim.Towers, t => t.Id.Value == SimE2Fixture.IdTower2);
    }

    [Fact]
    public void Combat_is_live_shields_burn_and_twins_fly()
    {
        var sim = new Simulation(SimE2Fixture.Config(), new SimRng(SimE2Fixture.Seed), SimE2Fixture.Seed);
        sim.EnqueueRange(SimE2Fixture.MilestoneCommands());

        bool sawBurn = false;
        bool sawTwin = false;
        bool sawKill = false;
        long lastKills = 0;
        for (long t = 1; t <= RunTicks; t++)
        {
            sim.Step();
            if (sim.Burn.ActiveCount > 0)
            {
                sawBurn = true;
            }

            if (sim.Projectiles.Count > 0)
            {
                sawTwin = true;
            }

            if (sim.KillCount > lastKills)
            {
                lastKills = sim.KillCount;
                sawKill = true;
            }
        }

        Assert.True(sim.ShotsLanded > 0, "塔群应有命中");
        Assert.True(sawKill, "应有击杀（护盾破后的结算）");
        Assert.True(sawBurn, "火油塔应点燃燃烧 DOT（splash+burn 链路活性）");
        Assert.True(sawTwin, "弹影层应有在飞弹（ProjectileTwinsEnabled）");
    }

    [Fact]
    public void Default_ruin_fraction_matches_failure_tiers_authority()
    {
        // 漂移守卫：SimulationConfig 默认残骸修复比例必须等于 Checkpoint 权威常量
        // （Sim 不能引用 Checkpoint——依赖方向相反；默认值镜像由本测试钉死）
        var config = SimE2Fixture.Config();
        Assert.Equal(
            EraCore.Checkpoint.FailureTiers.RepairCostFraction,
            new SimulationConfig(
                config.Width, config.Height, 0.0, 0.0, 0.0, config.BaseCell, config.EntryCells,
                config.TowerCatalog, config.Enemies, config.Waves, config.InitialStock).RuinRepairCostFraction);
    }
}
