using Xunit;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Legacy;
using EraCore.Persistence;
using EraCore.Tests.Checkpoint;
using EraCore.Tests.Persistence;

namespace EraCore.Tests.Legacy;

// [C-20][F-003][F-011][F-056][F-061] E1→E2 跃迁原子事务（T-074）：
// 事务断言矩阵——成功恰 70%/30%、硬前置拒绝、逐步注入回滚（字节级）、
// campaign 域恰一次写、前三关排期元数据。
// [Collection(CheckpointHeavy)]：每用例=SQLite 事务写+96×96 新大陆生成+回放，
// 属重载邻居——按 CheckpointHeavyCollection 的单邻居负载设计入串行集合，
// 避免与 spike-1 性能预算测试并行叠载（T-043 回归线口径）。
[Collection(CheckpointHeavyCollection.Name)]
public class EraTransitionTests : SqliteSavePortTestBase
{
    private long StoreFingerprint()
    {
        unchecked
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                string path = DbPhysicalPath + suffix;
                if (!File.Exists(path))
                {
                    hash = (hash ^ 0xFF) * prime;
                    continue;
                }

                foreach (byte b in Files.ReadAllBytes(Path.Combine("saves", Path.GetFileName(DbPhysicalPath) + suffix)))
                {
                    hash ^= b;
                    hash *= prime;
                }
            }

            return unchecked((long)hash);
        }
    }

    private CountingSavePort PreparedPort(CampaignMetaDto initial)
    {
        var port = new CountingSavePort(CreatePort());
        port.WriteCampaign(initial, SaveVersion.Current);
        port.CampaignWrites = 0; // 只统计事务内的写
        return port;
    }

    private static List<RelicCityBuildingFact> City() =>
        LegacyFixture.CityFacts(101, 48, 14, LegacyFixture.Mapping().Orientations);

    // ---- success ----------------------------------------------------- legal

    [Fact] // legal — 成功跃迁：资源恰 70% 留存/30% 带走，基金 3:1，campaign 域恰一次写
    public void successful_transition_splits_resources_exactly_70_30()
    {
        var oldPool = LegacyFixture.Pool(wood: 1000, stone: 700, food: 300);
        TechTreeLike(out var tech, out var ledger);
        var port = PreparedPort(LegacyFixture.EmptyMeta(LegacyFixture.Seed, 1));
        long fingerprintBefore = StoreFingerprint();

        EraTransitionResult result = new EraTransitionService().Execute(
            LegacyFixture.Request(oldPool, tech, ledger, City(), port, port.ReadCampaign(SaveVersion.Current)));

        // 留存侧恰 70%：old − floor(30%×old)，带走恰 floor(30%×old)
        var wood = new ResourceKey("era1:res-wood");
        var stone = new ResourceKey("era1:res-stone");
        var food = new ResourceKey("era1:res-food");
        Assert.Equal(300, result.Levy.Single(l => l.Resource.Equals(wood)).Carried);
        Assert.Equal(700, result.Levy.Single(l => l.Resource.Equals(wood)).Retained, 9);
        Assert.Equal(210, result.Levy.Single(l => l.Resource.Equals(stone)).Carried); // floor(700×0.3)
        Assert.Equal(490, result.Levy.Single(l => l.Resource.Equals(stone)).Retained, 9);
        Assert.Equal(90, result.Levy.Single(l => l.Resource.Equals(food)).Carried);
        Assert.Equal(210, result.Levy.Single(l => l.Resource.Equals(food)).Retained, 9);
        Assert.All(result.Levy, line => Assert.Equal(line.OldAmount, line.Carried + line.Retained, 9));

        // 留存侧留在旧池（T-074 验收「资源恰 70%」）
        Assert.Equal(700, oldPool.AmountOf(wood), 9);
        Assert.Equal(490, oldPool.AmountOf(stone), 9);
        Assert.Equal(210, oldPool.AmountOf(food), 9);

        // 有用资源随行入新池；无用途 food 3:1 折算基金（floor(90/3)=30）
        Assert.Equal(300, result.NewPool.AmountOf(wood), 9);
        Assert.Equal(210, result.NewPool.AmountOf(stone), 9);
        Assert.Equal(0, result.NewPool.AmountOf(food), 9);
        Assert.Equal(30, result.FundGained);
        Assert.Equal(30, ledger.Fund.Balance);

        // 新池口径：仓库等级 2 + 金字塔 +20%（附录 F.3 池上限公式）
        Assert.Equal(2, result.NewPool.WarehouseLevel);
        Assert.Equal(2000.0 * 2 * 1.2, result.NewPool.Capacity, 9);

        // campaign 域恰一次写；库内 meta=新代
        Assert.Equal(1, port.CampaignWrites);
        ICampaignMeta stored = port.ReadCampaign(SaveVersion.Current);
        Assert.Equal(2, stored.EraIndex);
        Assert.Equal(LegacyFixture.Seed, stored.WorldSeed);
        Assert.Equal(300, stored.ResourcePool["era1:res-wood"]);
        Assert.Equal(210, stored.ResourcePool["era1:res-stone"]);
        Assert.DoesNotContain("era1:res-food", stored.ResourcePool.Keys);
        Assert.NotEqual(fingerprintBefore, StoreFingerprint()); // 存储确实变了（成功路径）
    }

    [Fact] // legal — 遗迹/考古/奇观三断言入 meta：注册表含立碑+抽象清单，账本载荷可回放
    public void transition_carries_relic_archaeology_and_wonder_facts()
    {
        var oldPool = LegacyFixture.Pool(100, 100, 100);
        TechTreeLike(out var tech, out var ledger);
        var port = PreparedPort(LegacyFixture.EmptyMeta(LegacyFixture.Seed, 1));

        EraTransitionResult result = new EraTransitionService().Execute(
            LegacyFixture.Request(oldPool, tech, ledger, City(), port, port.ReadCampaign(SaveVersion.Current)));

        Assert.True(result.Mapping.IsNoLoss);
        ICampaignMeta stored = port.ReadCampaign(SaveVersion.Current);
        Assert.Contains(stored.RelicRegistry, entry => entry.StartsWith("relic|", StringComparison.Ordinal));
        // 无丢失计数律的存储侧投影：立碑+降级条目数=快照建筑数（降级产物=可考古条目）
        Assert.Equal(City().Count,
            stored.RelicRegistry.Count(entry => entry.StartsWith("relic|", StringComparison.Ordinal))
            + stored.RelicRegistry.Count(entry => entry.StartsWith("ruin|", StringComparison.Ordinal))
            + stored.RelicRegistry.Count(entry => entry.StartsWith("abstract|", StringComparison.Ordinal)));

        // 账本载荷经 K-7 layout 槽位回放：奇观/抽象清单/转移史完整
        var restored = new LegacyLedger(LegacyFixture.Archaeology(), LegacyFixture.Shop());
        ILevelLayoutSnapshot ledgerSlot = stored.LevelLayoutSnapshots.Single(s => s.LevelId == LegacyLedger.LedgerSlotId);
        restored.DecodeInto(ledgerSlot.LayoutPayload);
        Assert.Contains("campaign:wonder-pyramid", restored.BuiltWonderIds);
        Assert.Equal(result.Mapping.AbstractList, restored.AbstractList);
        Assert.Single(restored.Transitions, t => t.FromEra == 1 && t.ToEra == 2 && t.FundGained == result.FundGained);

        // 遗迹城快照槽位：A5 核心区显式持久化，可解码（载档后禁止几何重算的输入）
        ILevelLayoutSnapshot citySlot = stored.LevelLayoutSnapshots.Single(
            s => s.LevelId == EraTransitionService.RelicCitySlotId(1));
        RelicCitySnapshot decodedCity = RelicCityCodec.Decode(citySlot.LayoutPayload);
        Assert.True(decodedCity.CoreZoneCells.Count > 0);
        Assert.Equal(48, decodedCity.Buildings.Count);
    }

    [Fact] // legal — 科技时代根切换 + 前三关排期元数据（新能源/新物流/考古+英雄+系统）
    public void transition_switches_tech_root_and_publishes_first_three_schedule()
    {
        var oldPool = LegacyFixture.Pool(100, 100, 100);
        TechTreeLike(out var tech, out var ledger);
        var port = PreparedPort(LegacyFixture.EmptyMeta(LegacyFixture.Seed, 1));

        EraTransitionResult result = new EraTransitionService().Execute(
            LegacyFixture.Request(oldPool, tech, ledger, City(), port, port.ReadCampaign(SaveVersion.Current)));

        Assert.Equal(2, tech.EraIndex); // 科技时代根已切换
        Assert.Equal(2, result.ToEraIndex);
        Assert.Equal(3, result.Schedule.Count);
        Assert.Equal("new_energy", result.Schedule[0].Focus);
        Assert.Equal("campaign:level-e2-01", result.Schedule[0].LevelId);
        Assert.Equal("new_logistics", result.Schedule[1].Focus);
        Assert.Equal("archaeology_hero_system", result.Schedule[2].Focus);
    }

    // ---- hard preconditions ---------------------------------------- illegal

    [Fact] // illegal — 奇观缺拒（F.3 清单=跃迁硬条件）：无任何变异+字节级不变
    public void missing_wonder_refuses_without_any_mutation()
    {
        var oldPool = LegacyFixture.Pool(500, 500, 500);
        var tech = LegacyFixture.Tree(1, "campaign:level-e1-final");
        var ledger = new LegacyLedger(LegacyFixture.Archaeology(), LegacyFixture.Shop()); // 无奇观
        var port = PreparedPort(LegacyFixture.EmptyMeta(LegacyFixture.Seed, 1));
        long fingerprint = StoreFingerprint();

        EraTransitionRejectedException ex = Assert.Throws<EraTransitionRejectedException>(
            () => new EraTransitionService().Execute(
                LegacyFixture.Request(oldPool, tech, ledger, City(), port, port.ReadCampaign(SaveVersion.Current))));
        Assert.Contains("campaign:wonder-pyramid", ex.Message);

        Assert.Equal(0, port.CampaignWrites);
        Assert.Equal(fingerprint, StoreFingerprint()); // 字节级不变
        Assert.Equal(500, oldPool.AmountOf(new ResourceKey("era1:res-wood")), 9);
        Assert.Equal(1, tech.EraIndex);
    }

    [Fact] // illegal — 终局关未占领拒
    public void missing_final_level_refuses()
    {
        var oldPool = LegacyFixture.Pool(500, 500, 500);
        var tech = LegacyFixture.Tree(1); // 未捕获终局关
        var ledger = new LegacyLedger(LegacyFixture.Archaeology(), LegacyFixture.Shop());
        var port = PreparedPort(LegacyFixture.EmptyMeta(LegacyFixture.Seed, 1));
        long fingerprint = StoreFingerprint();

        EraTransitionRejectedException ex = Assert.Throws<EraTransitionRejectedException>(
            () => new EraTransitionService().Execute(
                LegacyFixture.Request(oldPool, tech, ledger, City(), port, port.ReadCampaign(SaveVersion.Current))));
        Assert.Contains("campaign:level-e1-final", ex.Message);
        Assert.Equal(0, port.CampaignWrites);
        Assert.Equal(fingerprint, StoreFingerprint());
    }

    [Fact] // illegal — 科技时代根与跳代不一致拒
    public void era_root_mismatch_refuses()
    {
        var oldPool = LegacyFixture.Pool(10, 10, 10);
        var tech = LegacyFixture.Tree(2, "campaign:level-e1-final"); // 已在 E2
        var ledger = new LegacyLedger(LegacyFixture.Archaeology(), LegacyFixture.Shop());
        ledger.MarkWonderBuilt(LegacyFixture.E1Wonder().Id);
        var port = PreparedPort(LegacyFixture.EmptyMeta(LegacyFixture.Seed, 1));

        Assert.Throws<EraTransitionRejectedException>(() => new EraTransitionService().Execute(
            LegacyFixture.Request(oldPool, tech, ledger, City(), port, port.ReadCampaign(SaveVersion.Current))));
        Assert.Equal(0, port.CampaignWrites);
    }

    // ---- step-injected rollback ----------------------------------- illegal

    [Theory] // illegal — 每步边界注入故障：字节级回滚+内存全量恢复（事务断言矩阵主体）
    [InlineData(EraTransitionStep.Precheck)]
    [InlineData(EraTransitionStep.Levy)]
    [InlineData(EraTransitionStep.Snapshot)]
    [InlineData(EraTransitionStep.NewContinent)]
    [InlineData(EraTransitionStep.RelicReplay)]
    [InlineData(EraTransitionStep.DigSiteBuild)] // T-125：挖掘位步（占用只落事务内大陆，无需 undo）
    [InlineData(EraTransitionStep.TechRootSwitch)]
    [InlineData(EraTransitionStep.ScheduleBuild)]
    public void failure_at_any_step_rolls_back_every_input(EraTransitionStep failAt)
    {
        var oldPool = LegacyFixture.Pool(999, 666, 333);
        TechTreeLike(out var tech, out var ledger);
        var port = PreparedPort(LegacyFixture.EmptyMeta(LegacyFixture.Seed, 1));
        long fingerprint = StoreFingerprint();
        long ledgerHash = ledger.ComputeStateHash();
        double woodBefore = oldPool.AmountOf(new ResourceKey("era1:res-wood"));

        var ex = Record.Exception(() => new EraTransitionService().Execute(
            LegacyFixture.Request(oldPool, tech, ledger, City(), port, port.ReadCampaign(SaveVersion.Current),
                probe: step =>
                {
                    if (step == failAt)
                    {
                        throw new InvalidOperationException($"injected failure at {step}");
                    }
                })));

        Assert.NotNull(ex);
        Assert.Contains(failAt.ToString(), ex.Message);
        Assert.Equal(0, port.CampaignWrites); // 恰零写
        Assert.Equal(fingerprint, StoreFingerprint()); // 字节级回滚断言
        Assert.Equal(999, oldPool.AmountOf(new ResourceKey("era1:res-wood")), 9); // 课税已撤
        Assert.Equal(666, oldPool.AmountOf(new ResourceKey("era1:res-stone")), 9);
        Assert.Equal(333, oldPool.AmountOf(new ResourceKey("era1:res-food")), 9);
        Assert.Equal(1, tech.EraIndex); // 时代根已恢复
        Assert.Equal(ledgerHash, ledger.ComputeStateHash()); // 账本字节级恢复
        Assert.Equal(woodBefore, oldPool.AmountOf(new ResourceKey("era1:res-wood")), 9);
        ICampaignMeta stored = port.ReadCampaign(SaveVersion.Current);
        Assert.Equal(1, stored.EraIndex); // 存储仍是旧代
    }

    [Fact] // illegal — 写入步本身失败（端口注入）：同样全量回滚+字节不变
    public void write_failure_rolls_back_completely()
    {
        var oldPool = LegacyFixture.Pool(800, 800, 800);
        TechTreeLike(out var tech, out var ledger);
        var port = PreparedPort(LegacyFixture.EmptyMeta(LegacyFixture.Seed, 1));
        port.ThrowOnWrite = true;
        long fingerprint = StoreFingerprint();
        long ledgerHash = ledger.ComputeStateHash();

        Assert.Throws<IOException>(() => new EraTransitionService().Execute(
            LegacyFixture.Request(oldPool, tech, ledger, City(), port, port.ReadCampaign(SaveVersion.Current))));

        Assert.Equal(0, port.CampaignWrites);
        Assert.Equal(fingerprint, StoreFingerprint());
        Assert.Equal(800, oldPool.AmountOf(new ResourceKey("era1:res-stone")), 9);
        Assert.Equal(1, tech.EraIndex);
        Assert.Equal(ledgerHash, ledger.ComputeStateHash());
    }

    // ---- carried families -------------------------------------------- legal

    [Fact] // legal — 已解锁科技与既有布局快照随 meta 传递（唯一 campaign 写里合流）
    public void unlocked_techs_and_prior_layouts_carry_into_the_single_write()
    {
        var oldPool = LegacyFixture.Pool(50, 50, 50);
        TechTreeLike(out var tech, out var ledger);
        var prior = new CampaignMetaDto(
            LegacyFixture.Seed, 1, new Dictionary<string, long>(),
            new List<string> { "campaign:tech-e1-arrow" },
            new List<string> { "relic|id=old|type=t|tier=T1|at=(1,1)|mode=home|shift=0" },
            new List<LevelLayoutSnapshotDto> { new("level.e1.loop01", new byte[] { 9, 9, 9 }) });
        var port = PreparedPort(prior);

        EraTransitionResult result = new EraTransitionService().Execute(
            LegacyFixture.Request(oldPool, tech, ledger, City(), port, prior));

        ICampaignMeta stored = port.ReadCampaign(SaveVersion.Current);
        Assert.Equal(1, port.CampaignWrites);
        Assert.Contains("campaign:tech-e1-arrow", stored.UnlockedTechs);
        Assert.Contains(stored.RelicRegistry, entry => entry.StartsWith("relic|id=old|")); // 旧注册表保留
        Assert.Contains(stored.LevelLayoutSnapshots, s => s.LevelId == "level.e1.loop01"); // 旧布局保留
        Assert.Equal(result.WrittenMeta.EraIndex, stored.EraIndex);
    }

    // ---- boundary ------------------------------------------------------

    [Fact] // boundary — 空资源池跃迁：零带走/零基金，事务仍完整成立
    public void empty_pool_transition_is_legal_and_free()
    {
        var oldPool = LegacyFixture.Pool(0, 0, 0);
        TechTreeLike(out var tech, out var ledger);
        var port = PreparedPort(LegacyFixture.EmptyMeta(LegacyFixture.Seed, 1));

        EraTransitionResult result = new EraTransitionService().Execute(
            LegacyFixture.Request(oldPool, tech, ledger, City(), port, port.ReadCampaign(SaveVersion.Current)));

        Assert.Empty(result.Levy);
        Assert.Equal(0, result.FundGained);
        Assert.Equal(2, result.ToEraIndex);
        Assert.True(result.Mapping.IsNoLoss);
    }

    [Fact] // boundary — 小额池向下取整：carried=0 时留存侧=全额
    public void small_amounts_floor_to_zero_carried()
    {
        var oldPool = LegacyFixture.Pool(wood: 3, stone: 0, food: 0); // floor(3×0.3)=0
        TechTreeLike(out var tech, out var ledger);
        var port = PreparedPort(LegacyFixture.EmptyMeta(LegacyFixture.Seed, 1));

        EraTransitionResult result = new EraTransitionService().Execute(
            LegacyFixture.Request(oldPool, tech, ledger, City(), port, port.ReadCampaign(SaveVersion.Current)));

        EraLevyLine wood = result.Levy.Single(l => l.Resource.Value == "era1:res-wood");
        Assert.Equal(0, wood.Carried);
        Assert.Equal(3, wood.Retained, 9);
        Assert.Equal(3, oldPool.AmountOf(new ResourceKey("era1:res-wood")), 9);
    }

    [Theory] // illegal — 请求域校验
    [InlineData(0)]
    [InlineData(6)]
    public void request_era_domain_enforced(int fromEra)
    {
        var pool = LegacyFixture.Pool(1, 1, 1);
        TechTreeLike(out var tech, out var ledger);
        var port = PreparedPort(LegacyFixture.EmptyMeta(LegacyFixture.Seed, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EraTransitionService().Execute(
            LegacyFixture.Request(pool, tech, ledger, City(), port, port.ReadCampaign(SaveVersion.Current))
            with { FromEraIndex = fromEra }));
    }

    private static void TechTreeLike(out EraCore.Progression.TechTree tech, out LegacyLedger ledger)
    {
        tech = LegacyFixture.Tree(1, "campaign:level-e1-final");
        ledger = new LegacyLedger(LegacyFixture.Archaeology(), LegacyFixture.Shop());
        ledger.MarkWonderBuilt(LegacyFixture.E1Wonder().Id);
    }
}
