using System.Security.Cryptography;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Journal;
using EraCore.Contracts.Sim;
using EraCore.Economy;
using EraCore.Sim;
using EraCore.Supply;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Agent.Pipeline;

// [C-13][F-069] 集成面（T-053）：JSONL 落盘可回放（读文件→重建决策序列 ==
// 原运行轨迹）+ 同 seed 同军令序列的字节级确定性 + DEBT 三件行为回归
// （①弹匣单源：Supply 侧计数 Sim 侧可见；②wrapper 唯一 tick：冷却不双计、
// 射击节奏不变；③TryPayCost/Place 窄窗：Place 失败补偿回滚库存复原）。
// [C-13][F-069]
public class JournalReplayIntegrationTests
{
    /// <summary>The canonical multi-order run every replay/determinism test replays.</summary>
    private static List<OrderOutcome> RunScript(PipelineHarness harness)
    {
        var outcomes = new List<OrderOutcome>();
        OrderOutcome build = harness.Pipeline.Handle("在东门建两座木箭塔");
        outcomes.Add(harness.Pipeline.Confirm(build.PendingId));
        outcomes.Add(harness.Pipeline.Handle("东门有什么威胁"));
        // Host-side wave hook: wave 2 refills the ledger and resets the 40%
        // wave cap (era-2 cap 2 pt = exactly one priced build per wave).
        Assert.True(harness.Pipeline.Ledger.BeginWave(2, isBossWave: false));
        OrderOutcome hold = harness.Pipeline.Handle("守住东门");
        outcomes.Add(harness.Pipeline.Refuse(hold.PendingId));
        outcomes.Add(harness.Pipeline.Handle("我们还剩多少木材"));
        outcomes.Add(harness.Pipeline.Handle("呃呃呃呃"));
        return outcomes;
    }

    [Fact] // legal：读 JSONL → 逐事件重建每条军令的决策序列 == 原运行轨迹
    public void Journal_on_disk_replays_to_the_original_decision_traces()
    {
        using var harness = new PipelineHarness();
        List<OrderOutcome> live = RunScript(harness);

        IReadOnlyList<JournalEvent> events = JsonlJournalWriter.ReadAll(harness.JournalPath);
        Assert.True(events.Count >= 20, $"战报事件过少：{events.Count}");

        // K-8 有序域：Seq 全程严格递增（管线+台账同一流）
        long last = 0;
        foreach (JournalEvent evt in events)
        {
            Assert.True(evt.Seq > last, $"Seq 回退：{last}->{evt.Seq}");
            last = evt.Seq;
        }

        // 四类冻结事件种类齐备（Input/Plan/Validation/Report）
        Assert.All(
            new[] { JournalEventKind.Input, JournalEventKind.Plan, JournalEventKind.Validation, JournalEventKind.Report },
            kind => Assert.Contains(events, evt => evt.Kind == kind));

        // 回放 == 原运行：逐军令比较完整决策轨迹（值相等 record）
        IReadOnlyList<DecisionTrace> replayed = JournalReplayer.Replay(harness.JournalPath);
        Assert.Equal(live.Count, replayed.Count);
        for (int i = 0; i < live.Count; i++)
        {
            Assert.True(live[i].Trace == replayed[i],
                $"军令#{i}「{live[i].OrderText}」回放漂移：live={Describe(live[i].Trace)} replay={Describe(replayed[i])}");
        }
    }

    [Fact] // legal：确定性——同 seed+同军令序列 → JSONL 字节一致（SHA-256）
    public void Same_seed_and_orders_produce_identical_jsonl_bytes()
    {
        string first;
        using (var harness = new PipelineHarness())
        {
            RunScript(harness);
            first = Sha256(harness.JournalPath);
        }

        string second;
        using (var harness = new PipelineHarness())
        {
            RunScript(harness);
            second = Sha256(harness.JournalPath);
        }

        Assert.Equal(first, second);
    }

    // ---- DEBT ①：弹匣单源（Supply.TowerMagazine 为权威，Sim 侧可见） -----------

    [Fact] // legal：民夫交付经权威弹匣，Sim 侧读同一计数器（改 Supply 侧计数 Sim 可见）
    public void Tower_magazine_is_the_single_delivery_source()
    {
        var simulation = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        simulation.EnqueueRange(SimE1Fixture.MilestoneCommands());
        simulation.Run(719); // 木材→加工→箭矢→塔 全链已有交付

        Assert.True(simulation.TryGetTowerMagazine(new EntityId(5), out TowerMagazine? magazine), "塔1（id=5）应有权威弹匣");
        Assert.Equal("era1:ammo-arrow", magazine!.Ammo.Value);
        var arrowPorter = simulation.Carriers[1]; // 民夫2：converter → tower1
        Assert.Equal(
            arrowPorter.DeliveredItems,
            magazine.ItemsAccepted); // 同一交付只记一次——单源成立
        Assert.Equal(
            magazine.ItemsAccepted * AmmoConverter.ShotsPerItem,
            magazine.CumulativeShotsDelivered); // 交付流计数=监测器轮询源

        Assert.False(simulation.TryGetTowerMagazine(new EntityId(3), out _), "采集建筑没有弹匣");
    }

    // ---- DEBT ②：SuppliedWeaponFire 接线（wrapper 唯一 tick，冷却不双计） ------

    [Fact] // legal：wrapper 是唯一 WeaponFire.Tick 调用者——冷却每 Step 恰 −1（节奏不变）
    public void Pacer_is_the_unique_weapon_ticker()
    {
        var simulation = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        simulation.Enqueue(new SimCommand(1, SimCommandKind.PlayerBuild, BuildTower(6, 21)));
        simulation.Run(1);
        Assert.Single(simulation.Towers);

        var tower = simulation.Towers[0];
        Assert.True(simulation.TryGetFirePacer(tower.Id, out SuppliedWeaponFire? pacer));
        Assert.Same(tower.Weapon, pacer!.Weapon); // wrapper 包的就是 Sim 塔那把武器——无第二计时者
        Assert.Contains(tower.Id, simulation.SupplyMonitor.Towers); // 塔已在监测器注册（需求侧接线）

        // 供弹 1 件=10 发，主动开一枪武装 30t 冷却，然后连走 5 个 Step：
        // 冷却必须恰好 30−5=25（每 tick 恰一次推进——双计冷却会掉 10）
        Assert.True(simulation.TryGetTowerMagazine(tower.Id, out TowerMagazine? magazine));
        Assert.Equal(1, magazine!.TryAccept(new ResourceKey("era1:ammo-arrow"), 1));
        Assert.NotNull(pacer.TryFire(new EntityId(999_999), simulation.CurrentTick));
        Assert.Equal(tower.Spec.ReloadTicks!.Value, tower.Weapon.CooldownTicksRemaining);

        simulation.Run(5);
        Assert.Equal(25, tower.Weapon.CooldownTicksRemaining);
        Assert.Equal(1, tower.Weapon.FiredCount); // 期间无目标——不追发
    }

    [Fact] // legal：效率 1（暖机内）时 wrapper 节奏=裸武器节奏（射击节奏不变）
    public void Paced_cadence_matches_the_bare_loop_at_full_efficiency()
    {
        var simulation = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        simulation.Enqueue(new SimCommand(1, SimCommandKind.PlayerBuild, BuildTower(6, 21)));
        simulation.Run(1);
        var tower = simulation.Towers[0];
        Assert.True(simulation.TryGetTowerMagazine(tower.Id, out TowerMagazine? magazine));
        Assert.Equal(2, magazine!.TryAccept(new ResourceKey("era1:ammo-arrow"), 2)); // 20 发

        // 同一塔同一弹匣：直调裸武器 10 tick 的射击数 == wrapper 代言的塔走 10 tick 的射击数
        int bareBefore = tower.Weapon.FiredCount;
        for (int i = 0; i < 10; i++)
        {
            tower.Weapon.Tick();
            if (tower.Weapon.ReadyToFire)
            {
                _ = tower.Weapon.TryFire(new EntityId(999_999), simulation.CurrentTick);
            }
        }

        int bareFired = tower.Weapon.FiredCount - bareBefore;
        Assert.True(bareFired >= 1, "裸循环样本应至少开一枪");

        // 换一座塔（清弹匣差异）走 wrapper 通道对比
        simulation.Enqueue(new SimCommand(simulation.CurrentTick + 1, SimCommandKind.PlayerBuild, BuildTower(9, 21)));
        simulation.Run(1);
        var wrapped = simulation.Towers[1];
        Assert.True(simulation.TryGetFirePacer(wrapped.Id, out SuppliedWeaponFire? pacer));
        Assert.True(simulation.TryGetTowerMagazine(wrapped.Id, out TowerMagazine? wrappedMagazine));
        Assert.Equal(2, wrappedMagazine!.TryAccept(new ResourceKey("era1:ammo-arrow"), 2));

        int wrappedBefore = wrapped.Weapon.FiredCount;
        for (int i = 0; i < 10; i++)
        {
            simulation.Step(); // 编排器通道：monitor.Tick + pacer.Tick + （无目标不追发）
            if (wrapped.Weapon.ReadyToFire)
            {
                _ = pacer!.TryFire(new EntityId(999_999), simulation.CurrentTick);
            }
        }

        Assert.Equal(bareFired, wrapped.Weapon.FiredCount - wrappedBefore); // 节奏一致=无双计
    }

    private static string BuildTower(int x, int y) =>
        System.Text.Json.JsonSerializer.Serialize(
            new BuildTowerCommandJson("build-tower", "era1:tower-wood-arrow", new CellJson(x, y)),
            PipelinePlannerJsonContext.Default.BuildTowerCommandJson);

    private static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new System.IO.FileStream(
            path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static string Describe(DecisionTrace trace) =>
        $"{trace.OrderText}|{trace.Outcome}|steps={trace.Steps.Count}|approval={trace.ApprovalDecision}"
        + $"|exec={string.Join(",", trace.Steps.Select(s => s.Verb + ":" + s.Status + "x" + s.Attempts.Count))}"
        + $"|report={trace.ReportSummary}";
}
