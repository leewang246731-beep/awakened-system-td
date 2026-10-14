using EraCore.Sim;
using EraCore.Sim.EntityGroups;
using Xunit;

namespace EraCore.Tests.Sim;

// [C-12][F-155] 分组容器形态（T-067 任务卡四组：Units（含英雄）/Projectiles
// （弹影层）/Buildings/Carriers——按类型分组的实现形态断言）。
public class SimEntityGroupsTests
{
    [Fact] // legal — 四族分组的类型化形态：单位族（敌+英雄）、弹影、建筑、运输
    public void The_four_card_families_exist_with_their_typed_slots()
    {
        var groups = new SimEntityGroups();

        // Units 族：敌方单位组 + 英雄槽（英雄与敌人同为单位层、类型不同分组承载）
        Assert.IsType<EntityGroup<EraCore.Units.Unit>>(groups.Units);
        Assert.IsType<EntityGroup<EraCore.Units.Hero>>(groups.Heroes);

        // Projectiles 族：弹影层（K-6 载体）
        Assert.IsType<EntityGroup<SimProjectile>>(groups.Projectiles);

        // Buildings 族：放置序薄记行 + 各子系统引擎对象组
        Assert.IsType<EntityGroup<SimBuilding>>(groups.Buildings);
        Assert.IsType<EntityGroup<SimTower>>(groups.Towers);
        Assert.IsType<EntityGroup<EraCore.Economy.Producer>>(groups.Producers);
        Assert.IsType<EntityGroup<EraCore.Economy.AmmoConverter>>(groups.Converters);

        // Carriers 族：档位化行程运输 + 传送带 + 传送阵
        Assert.IsType<EntityGroup<EraCore.Logistics.Carrier>>(groups.Carriers);
        Assert.IsType<EntityGroup<EraCore.Logistics.ConveyorBelt>>(groups.Belts);
        Assert.IsType<EntityGroup<SimTeleporterLane>>(groups.Teleporters);
    }

    [Fact] // legal — 各组独立计数（一族注册不影响他族）
    public void Groups_count_independently()
    {
        var groups = new SimEntityGroups();
        groups.Units.Add(new EraCore.Units.Unit(
            new EraCore.Contracts.Foundation.EntityId(1), "t:u", 0f, 0f, 1, 0, 1f));

        Assert.True(groups.Units.Count == 1, $"Units 族应计 1（实际 {groups.Units.Count}）");
        Assert.True(groups.Heroes.Count == 0, "Heroes 族应计 0");
        Assert.True(groups.Projectiles.Count == 0, "Projectiles 族应计 0");
        Assert.True(groups.Buildings.Count == 0, "Buildings 族应计 0");
        Assert.True(groups.Carriers.Count == 0, "Carriers 族应计 0");
        Assert.True(groups.Teleporters.Count == 0, "Teleporters 族应计 0");
    }
}
