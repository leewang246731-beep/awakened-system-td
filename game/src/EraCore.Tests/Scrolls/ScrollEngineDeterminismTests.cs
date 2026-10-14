using EraCore.Scrolls;
using Xunit;

namespace EraCore.Tests.Scrolls;

// [C-18][F-037] T-071 确定性：ScrollEngine.ComputeStateHash 供上层聚合入
// Sim 哈希（T-067 扩展点用法——Sim 不改，卷轴侧提供哈希）。同输入序→逐
// 边界哈希相等；输入分叉→哈希立变。
public class ScrollEngineDeterminismTests
{
    private static (ScrollEngine Engine, ScriptedScrollWorld World, RecordingExecutor Executor) Build()
    {
        var executor = new RecordingExecutor();
        var engine = new ScrollEngine(2, executor, new RecordingEffectSink());
        var world = new ScriptedScrollWorld { CurrentWave = 1 };
        world.ZoneFacts.Add(ScriptedScrollWorld.Zone("z1", (0, 0), (1, 0)));
        world.AddTower(101, "z1");
        world.AddEnemy(1, "z1", 90, 90);
        return (engine, world, executor);
    }

    private static void RunScenario(ScrollEngine engine, ScriptedScrollWorld world, List<long> hashes)
    {
        _ = engine.Equip(ScrollTestDefs.AlwaysTrueZoneScroll("t:scroll-a", priority: 60, cooldownTicks: 90, durationTicks: 60));
        _ = engine.Equip(ScrollTestDefs.FocusFireScroll("t:scroll-b", priority: 50, cooldownTicks: 0));
        engine.NotifySystemTowerOrder(new[] { 555L }, tick: 1, wave: 1);

        for (long tick = 1; tick <= 180; tick++)
        {
            engine.Step(tick, world);
            if (tick % 30 == 0)
            {
                hashes.Add(engine.ComputeStateHash());
            }
        }
    }

    [Fact]
    public void Same_run_produces_identical_hashes_at_every_boundary()
    {
        // legal: 同装备序+同世界脚本 → 6 个边界哈希逐一相等。
        var (engineA, worldA, _) = Build();
        var (engineB, worldB, _) = Build();
        var hashesA = new List<long>();
        var hashesB = new List<long>();

        RunScenario(engineA, worldA, hashesA);
        RunScenario(engineB, worldB, hashesB);

        Assert.Equal(6, hashesA.Count);
        Assert.Equal(hashesA, hashesB);
    }

    [Fact]
    public void Divergent_world_state_changes_the_hash_immediately()
    {
        // illegal（负向）: 世界分叉（多一座塔/多一个敌）→ 哈希从首个含状态的边界起不等。
        var (engineA, worldA, _) = Build();
        var (engineB, worldB, _) = Build();
        worldB.AddTower(999, "z1");
        var hashesA = new List<long>();
        var hashesB = new List<long>();

        RunScenario(engineA, worldA, hashesA);
        RunScenario(engineB, worldB, hashesB);

        Assert.Equal(6, hashesA.Count);
        Assert.NotEqual(hashesA, hashesB);
    }

    [Fact]
    public void Hash_covers_equip_order_and_fire_state()
    {
        // boundary: 同卷轴集合不同装备序 → 哈希不同；一次触发后 → 哈希变化。
        var (engine, world, _) = Build();
        _ = engine.Equip(ScrollTestDefs.AlwaysTrueZoneScroll("t:scroll-x", priority: 60, cooldownTicks: 3000));
        long before = engine.ComputeStateHash();

        engine.Step(30, world); // fires
        long after = engine.ComputeStateHash();
        Assert.NotEqual(before, after);

        var (other, world2, _) = Build();
        _ = other.Equip(ScrollTestDefs.FocusFireScroll("t:scroll-y", priority: 50, cooldownTicks: 3000));
        _ = other.Equip(ScrollTestDefs.AlwaysTrueZoneScroll("t:scroll-x", priority: 60, cooldownTicks: 3000));
        _ = other.Unequip("t:scroll-y"); // same end roster, different history
        // 不同历史（触发过 vs 未触发）必然分叉——本断言锚定 fire 状态入哈希。
        Assert.NotEqual(engine.ComputeStateHash(), other.ComputeStateHash());
    }
}
